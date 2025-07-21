use clap::Parser;
use clowd_capture::cli::ProgramArgs;

fn main() {
    let args = ProgramArgs::parse();
    clowd_capture::start(args);
}