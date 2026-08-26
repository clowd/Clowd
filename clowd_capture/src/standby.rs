//! Cross-platform standby control plane. Newline-delimited JSON on stdin carries
//! complete CLI snapshots and explicit capture requests; EOF means the shell died.

use std::io::{BufRead, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant, SystemTime};

use clap::Parser;
use global_hotkey::hotkey::HotKey;
use global_hotkey::{GlobalHotKeyEvent, GlobalHotKeyManager, HotKeyState};
use serde::Deserialize;
use winit::application::ApplicationHandler;
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::platform::run_on_demand::EventLoopExtRunOnDemand;

use crate::settings::{CaptureMode, CliArgs};

#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum Message {
    Settings { revision: u64, args: Vec<String> },
    Capture { mode: String },
}

enum Input {
    Message(Message),
    Invalid(String),
}

pub struct Standby {
    input_rx: mpsc::Receiver<Input>,
    parent_gone: Arc<AtomicBool>,
    capture_active: Arc<AtomicBool>,
    manager: GlobalHotKeyManager,
    hotkeys: Vec<(HotKey, CaptureMode)>,
}

impl Standby {
    pub fn new() -> anyhow::Result<Self> {
        let parent_gone = Arc::new(AtomicBool::new(false));
        let capture_active = Arc::new(AtomicBool::new(false));
        let (input_tx, input_rx) = mpsc::channel();
        let parent_gone_reader = Arc::clone(&parent_gone);
        let capture_active_reader = Arc::clone(&capture_active);
        std::thread::Builder::new()
            .name("standby-ipc".into())
            .spawn(move || {
                for line in std::io::stdin().lock().lines() {
                    match line {
                        Ok(line) if !line.trim().is_empty() => {
                            let input = match serde_json::from_str(&line) {
                                Ok(message) => Input::Message(message),
                                Err(err) => Input::Invalid(err.to_string()),
                            };
                            if input_tx.send(input).is_err() {
                                return;
                            }
                        }
                        Ok(_) => {}
                        Err(err) => {
                            let _ = input_tx.send(Input::Invalid(err.to_string()));
                            break;
                        }
                    }
                }
                parent_gone_reader.store(true, Ordering::Release);
                if capture_active_reader.load(Ordering::Acquire) {
                    std::process::exit(0);
                }
            })?;
        Ok(Self {
            input_rx,
            parent_gone,
            capture_active,
            manager: GlobalHotKeyManager::new()?,
            hotkeys: Vec::new(),
        })
    }

    /// Returns false when the shell closed stdin before a capture was requested.
    pub fn wait(&mut self, args: &mut CliArgs, event_loop: &mut EventLoop<()>) -> anyhow::Result<bool> {
        self.capture_active
            .store(false, Ordering::Release);

        // Hotkeys remain registered while the overlay is active so another
        // application cannot claim them. Events accumulated during that time
        // are deliberately discarded instead of becoming another capture.
        while GlobalHotKeyEvent::receiver()
            .try_recv()
            .is_ok()
        {}
        if self.hotkeys.is_empty() {
            let (hotkeys, _) = register_hotkeys(&self.manager, args);
            self.hotkeys = hotkeys;
        }

        println!("CLOWD_STANDBY_READY");
        std::io::stdout().flush()?;
        let mut app = StandbyApp {
            args,
            manager: &self.manager,
            hotkeys: &mut self.hotkeys,
            input_rx: &self.input_rx,
            parent_gone: &self.parent_gone,
            selected: None,
        };
        event_loop.run_app_on_demand(&mut app)?;

        let Some(selected) = app.selected else {
            return Ok(false);
        };
        self.capture_active
            .store(true, Ordering::Release);
        println!(
            "CLOWD_HOTKEY {}",
            match selected {
                CaptureMode::Region => "region",
                CaptureMode::Window => "window",
                CaptureMode::Screen => "monitor",
            }
        );
        std::io::stdout().flush()?;
        let root = app
            .args
            .session_root
            .as_deref()
            .expect("clap requires session root");
        let session = create_session_dir(root)?;
        app.args.session_dir = Some(session.clone());
        app.args.capture_mode = selected;
        println!("CLOWD_SESSION {}", session.display());
        std::io::stdout().flush()?;
        Ok(true)
    }
}

struct StandbyApp<'a> {
    args: &'a mut CliArgs,
    manager: &'a GlobalHotKeyManager,
    hotkeys: &'a mut Vec<(HotKey, CaptureMode)>,
    input_rx: &'a mpsc::Receiver<Input>,
    parent_gone: &'a AtomicBool,
    selected: Option<CaptureMode>,
}

impl StandbyApp<'_> {
    fn apply_settings(&mut self, revision: u64, raw_args: Vec<String>) {
        let parsed = CliArgs::try_parse_from(std::iter::once("clowd_capture_wgpu".to_string()).chain(raw_args));
        match parsed {
            Ok(next) if next.standby => {
                let _ = self.manager.unregister_all(
                    &self
                        .hotkeys
                        .iter()
                        .map(|(hotkey, _)| *hotkey)
                        .collect::<Vec<_>>(),
                );
                let (next_hotkeys, statuses) = register_hotkeys(&self.manager, &next);
                *self.hotkeys = next_hotkeys;
                *self.args = next;
                report_settings_status(revision, true, None, statuses);
            }
            Ok(_) => report_settings_status(
                revision,
                false,
                Some("settings snapshot must include --standby".into()),
                failed_statuses("Settings snapshot was rejected."),
            ),
            Err(err) => report_settings_status(
                revision,
                false,
                Some(err.to_string()),
                failed_statuses("Settings snapshot could not be parsed."),
            ),
        }
    }
}

impl ApplicationHandler for StandbyApp<'_> {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        event_loop.set_control_flow(ControlFlow::WaitUntil(Instant::now() + Duration::from_millis(50)));
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        if self.parent_gone.load(Ordering::Acquire) {
            event_loop.exit();
            return;
        }
        while let Ok(input) = self.input_rx.try_recv() {
            match input {
                Input::Message(Message::Settings {
                    revision,
                    args,
                }) => self.apply_settings(revision, args),
                Input::Message(Message::Capture {
                    mode,
                }) => {
                    self.selected = match mode.as_str() {
                        "region" => Some(CaptureMode::Region),
                        "window" => Some(CaptureMode::Window),
                        "screen" | "monitor" => Some(CaptureMode::Screen),
                        _ => {
                            println!("CLOWD_IPC_ERROR unknown capture mode '{mode}'");
                            let _ = std::io::stdout().flush();
                            None
                        }
                    };
                    if self.selected.is_some() {
                        event_loop.exit();
                        return;
                    }
                }
                Input::Invalid(err) => {
                    println!("CLOWD_IPC_ERROR {err}");
                    let _ = std::io::stdout().flush();
                }
            }
        }
        while let Ok(event) = GlobalHotKeyEvent::receiver().try_recv() {
            if event.state == HotKeyState::Pressed {
                if let Some((_, mode)) = self
                    .hotkeys
                    .iter()
                    .find(|(hotkey, _)| hotkey.id() == event.id)
                {
                    self.selected = Some(*mode);
                    event_loop.exit();
                    return;
                }
            }
        }
        event_loop.set_control_flow(ControlFlow::WaitUntil(Instant::now() + Duration::from_millis(50)));
    }

    fn window_event(&mut self, _event_loop: &ActiveEventLoop, _window_id: winit::window::WindowId, _event: winit::event::WindowEvent) {}
}

fn register_hotkeys(
    manager: &GlobalHotKeyManager,
    args: &CliArgs,
) -> (Vec<(HotKey, CaptureMode)>, serde_json::Map<String, serde_json::Value>) {
    let requested = [
        ("main", args.hk_main.as_deref(), CaptureMode::Region),
        ("window", args.hk_window.as_deref(), CaptureMode::Window),
        ("monitor", args.hk_monitor.as_deref(), CaptureMode::Screen),
    ];
    let mut registered = Vec::new();
    let mut statuses = serde_json::Map::new();
    for (name, gesture, mode) in requested {
        let (active, error) = match gesture {
            None => (false, None),
            Some(gesture) => match parse_gesture(gesture) {
                Err(err) => (false, Some(err.to_string())),
                Ok(hotkey) => match manager.register(hotkey) {
                    Ok(()) => {
                        registered.push((hotkey, mode));
                        (true, None)
                    }
                    Err(err) => (false, Some(err.to_string())),
                },
            },
        };
        statuses.insert(name.into(), serde_json::json!({ "active": active, "error": error }));
    }
    (registered, statuses)
}

fn failed_statuses(error: &str) -> serde_json::Map<String, serde_json::Value> {
    ["main", "window", "monitor"]
        .into_iter()
        .map(|name| (name.into(), serde_json::json!({ "active": false, "error": error })))
        .collect()
}

fn report_settings_status(revision: u64, applied: bool, error: Option<String>, hotkeys: serde_json::Map<String, serde_json::Value>) {
    println!(
        "CLOWD_SETTINGS_STATUS {}",
        serde_json::json!({ "revision": revision, "applied": applied, "error": error, "hotkeys": hotkeys })
    );
    let _ = std::io::stdout().flush();
}

fn create_session_dir(root: &Path) -> anyhow::Result<PathBuf> {
    std::fs::create_dir_all(root)?;
    let stamp = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)?
        .as_nanos();
    let path = root.join(format!("capture-{stamp}-{}", std::process::id()));
    std::fs::create_dir(&path)?;
    Ok(path)
}

fn parse_gesture(value: &str) -> anyhow::Result<HotKey> {
    let normalized = value
        .replace("Snapshot", "PrintScreen")
        .replace("Meta", "Super");
    normalized.parse().map_err(Into::into)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_clowd_gestures() {
        assert!(parse_gesture("Snapshot").is_ok());
        assert!(parse_gesture("Control+Shift+Snapshot").is_ok());
        assert!(parse_gesture("Alt+F12").is_ok());
        assert!(parse_gesture("Control+A").is_ok());
    }
}
