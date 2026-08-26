use std::fs::File;
use std::io::{LineWriter, Write};
use std::path::Path;
use std::sync::{Arc, Mutex};

/// Process-lifetime logger with a per-capture file mirror. The global `log`
/// facade can only be installed once, so cycling swaps the writer under it.
pub struct CycleLogger {
    terminal: Box<dyn log::Log>,
    file: Mutex<Option<LineWriter<File>>>,
}

struct InstalledLogger(Arc<CycleLogger>);

impl CycleLogger {
    pub fn install() -> Arc<Self> {
        let logger = Arc::new(Self {
            terminal: simplelog::TermLogger::new(
                log::LevelFilter::Info,
                simplelog::Config::default(),
                simplelog::TerminalMode::Mixed,
                simplelog::ColorChoice::Auto,
            ),
            file: Mutex::new(None),
        });
        clowd_rust_core::telemetry::install_logger(Box::new(InstalledLogger(Arc::clone(&logger))));
        logger
    }

    pub fn begin_session(&self, dir: &Path) {
        let writer = File::create(dir.join("capture.log")).map(LineWriter::new);
        *self
            .file
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = writer.ok();
    }

    pub fn end_session(&self) {
        if let Some(mut writer) = self
            .file
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            let _ = writer.flush();
        }
    }
}

impl log::Log for InstalledLogger {
    fn enabled(&self, metadata: &log::Metadata<'_>) -> bool {
        self.0.terminal.enabled(metadata)
    }

    fn log(&self, record: &log::Record<'_>) {
        self.0.terminal.log(record);
        if self.enabled(record.metadata()) {
            if let Some(writer) = self
                .0
                .file
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .as_mut()
            {
                let _ = writeln!(writer, "[{}] {}", record.level(), record.args());
            }
        }
    }

    fn flush(&self) {
        self.0.terminal.flush();
        if let Some(writer) = self
            .0
            .file
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .as_mut()
        {
            let _ = writer.flush();
        }
    }
}
