use anyhow::Result;
use global_hotkey::{
    hotkey::{Code, HotKey, Modifiers},
    GlobalHotKeyManager,
};
use serde::{Deserialize, Serialize};
use std::{
    path::PathBuf,
    sync::{Mutex, RwLock},
};

fn default_pattern() -> String {
    "clowd_%Y-%m-%d_%H-%M-%S".to_string()
}

fn default_save_dir() -> PathBuf {
    dirs::picture_dir().unwrap_or_else(|| dirs::home_dir().unwrap())
}

fn default_session_dir() -> PathBuf {
    let mut parent_dir = std::env::current_exe().unwrap();
    parent_dir.pop();
    parent_dir.pop();
    parent_dir.join("sessions")
}

fn default_view_data_dir() -> PathBuf {
    let mut parent_dir = std::env::current_exe().unwrap();
    parent_dir.pop();
    parent_dir.pop();
    parent_dir.join("view_data")
}

// NOTE: anything that is not an Option<T> must have a default attribute
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct ClowdSettings {
    pub hotkey_capture: Option<HotKey>,
    pub hotkey_colorpick: Option<HotKey>,
    #[serde(default = "default_pattern")]
    pub filename_pattern: String,
    #[serde(default = "default_session_dir")]
    pub session_dir: PathBuf,
    #[serde(default = "default_view_data_dir")]
    pub webview_data_dir: PathBuf,
    #[serde(default = "default_save_dir")]
    pub last_capture_save_dir: PathBuf,
    #[serde(default)]
    pub no_check_updates: bool,
    #[serde(default)]
    pub no_start_on_login: bool,
    #[serde(default)]
    pub no_open_saved_in_explorer: bool,
}

pub type ClowdSettingsMutex = RwLock<ClowdSettings>;

impl Default for ClowdSettings {
    fn default() -> Self {
        ClowdSettings {
            hotkey_capture: Some(HotKey::new(None, Code::PrintScreen)),
            hotkey_colorpick: Some(HotKey::new(Some(Modifiers::SHIFT), Code::PrintScreen)),
            filename_pattern: default_pattern(),
            session_dir: default_session_dir(),
            webview_data_dir: default_view_data_dir(),
            last_capture_save_dir: default_save_dir(),
            no_check_updates: false,
            no_start_on_login: false,
            no_open_saved_in_explorer: false,
        }
    }
}

impl ClowdSettings {
    pub fn try_save(&self) -> Result<()> {
        let parent_dir = self.session_dir.parent().unwrap();
        let settings_path = parent_dir.join("clowd_settings.json");
        let s = serde_json::to_string_pretty(self)?;
        std::fs::write(settings_path, s)?;
        Ok(())
    }

    pub fn save(&self) {
        if let Err(e) = self.try_save() {
            error!("Error saving settings: {}", e);
        }
    }
}

pub fn load_settings_or_default() -> RwLock<ClowdSettings> {
    let mut parent_dir = std::env::current_exe().unwrap();
    parent_dir.pop();
    parent_dir.pop();

    let settings_path = parent_dir.join("clowd_settings.json");

    let settings = match std::fs::read_to_string(&settings_path) {
        Ok(s) => match serde_json::from_str(&s) {
            Ok(s) => s,
            Err(e) => {
                error!("Error loading settings from {:?}: {}", settings_path, e);
                ClowdSettings::default()
            }
        },
        Err(e) => {
            error!("Error loading settings from {:?}: {}", settings_path, e);
            ClowdSettings::default()
        }
    };

    RwLock::new(settings)
}

pub struct HotkeyManager(Mutex<(GlobalHotKeyManager, Vec<HotKey>)>);
unsafe impl Send for HotkeyManager {}
unsafe impl Sync for HotkeyManager {}

impl Default for HotkeyManager {
    fn default() -> Self {
        Self(Mutex::new((GlobalHotKeyManager::new().unwrap(), vec![])))
    }
}

impl HotkeyManager {
    pub fn unregister_all(&self) {
        let mut manager = self.0.lock().unwrap();
        let hotkeys = manager.1.drain(..).collect::<Vec<_>>();
        for hotkey in hotkeys {
            if let Err(e) = manager.0.unregister(hotkey) {
                error!("Error unregistering hotkey: {}", e);
            }
        }
    }
    pub fn register_all(&self, hotkeys: &[HotKey]) {
        let mut manager = self.0.lock().unwrap();
        for hotkey in hotkeys {
            if let Err(e) = manager.0.register(hotkey.clone()) {
                error!("Error registering hotkey: {}", e);
            } else {
                manager.1.push(hotkey.clone());
            }
        }
    }
}
