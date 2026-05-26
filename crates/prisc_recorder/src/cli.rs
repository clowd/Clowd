use std::path::PathBuf;
use clap::Parser;

#[derive(Parser, Debug)]
#[command(name = "obs-express", about = "Minimal screen recorder backed by OBS")]
pub struct Cli {
    #[arg(long)]
    pub output: PathBuf,

    #[arg(long)]
    pub monitor: Option<String>,

    #[arg(long)]
    pub region: Option<String>,

    #[arg(long, default_value = "30")]
    pub fps: u32,

    #[arg(long, default_value = "24")]
    pub crf: u16,

    #[arg(long)]
    pub max_width: Option<u32>,

    #[arg(long)]
    pub max_height: Option<u32>,

    #[arg(long)]
    pub hw_accel: bool,

    #[arg(long)]
    pub low_cpu: bool,

    #[arg(long)]
    pub no_cursor: bool,

    #[arg(long)]
    pub start_paused: bool,

    #[arg(long)]
    pub speaker: Vec<String>,

    #[arg(long)]
    pub microphone: Vec<String>,
}
