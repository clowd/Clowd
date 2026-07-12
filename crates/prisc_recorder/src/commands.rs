/// Commands consumed by the recorder's run loop. Most originate from stdin
/// (§1.2); `OutputStarted` / `OutputStopped` are injected by the OBS output's
/// start/stop signal handlers and are never parsed from stdin.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Command {
    Start,
    Pause,
    Quit,
    MuteSpeaker(usize),
    UnmuteSpeaker(usize),
    MuteMic(usize),
    UnmuteMic(usize),
    /// OBS "start" signal fired — recording is actually rolling.
    OutputStarted,
    /// OBS "stop" signal fired with the given stop code (spontaneous or in
    /// response to our own `obs_output_stop`).
    OutputStopped(i64),
}

/// Whitespace-split, first token dispatched case-insensitively. Unknown lines
/// return `None` (the caller logs and ignores them).
pub fn parse_command(line: &str) -> Option<Command> {
    let mut parts = line.split_whitespace();
    let head = parts.next()?.to_ascii_lowercase();
    let arg = parts.next();
    let index = || arg?.parse::<usize>().ok();
    match head.as_str() {
        "start" => Some(Command::Start),
        "pause" => Some(Command::Pause),
        "quit" | "q" => Some(Command::Quit),
        "mute-speaker" => index().map(Command::MuteSpeaker),
        "unmute-speaker" => index().map(Command::UnmuteSpeaker),
        "mute-mic" => index().map(Command::MuteMic),
        "unmute-mic" => index().map(Command::UnmuteMic),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_basic_commands() {
        assert_eq!(parse_command("start"), Some(Command::Start));
        assert_eq!(parse_command("  pause  "), Some(Command::Pause));
        assert_eq!(parse_command("quit"), Some(Command::Quit));
        assert_eq!(parse_command("q"), Some(Command::Quit));
        assert_eq!(parse_command("QUIT"), Some(Command::Quit)); // case-insensitive
        assert_eq!(
            parse_command("mute-speaker 1"),
            Some(Command::MuteSpeaker(1))
        );
        assert_eq!(parse_command("unmute-mic 0"), Some(Command::UnmuteMic(0)));
    }

    #[test]
    fn rejects_unknown_and_malformed() {
        assert_eq!(parse_command(""), None);
        assert_eq!(parse_command("bogus"), None);
        assert_eq!(parse_command("mute-speaker"), None);
        assert_eq!(parse_command("mute-speaker abc"), None);
    }
}
