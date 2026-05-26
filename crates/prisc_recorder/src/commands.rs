pub enum Command {
    Start,
    Pause,
    Quit,
    MuteSpeaker(usize),
    UnmuteSpeaker(usize),
    MuteMic(usize),
    UnmuteMic(usize),
}

pub fn parse_command(line: &str) -> Option<Command> {
    let parts: Vec<&str> = line.trim().split_whitespace().collect();
    match parts.first().map(|s| *s) {
        Some("start") => Some(Command::Start),
        Some("pause") => Some(Command::Pause),
        Some("quit") => Some(Command::Quit),
        Some("mute-speaker") => parts.get(1)?.parse().ok().map(Command::MuteSpeaker),
        Some("unmute-speaker") => parts.get(1)?.parse().ok().map(Command::UnmuteSpeaker),
        Some("mute-mic") => parts.get(1)?.parse().ok().map(Command::MuteMic),
        Some("unmute-mic") => parts.get(1)?.parse().ok().map(Command::UnmuteMic),
        _ => None,
    }
}
