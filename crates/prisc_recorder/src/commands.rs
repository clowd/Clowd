/// Commands consumed by the recorder's run loop. Most originate from stdin
/// (§1.2); `OutputStarted` / `OutputStopped` are injected by the OBS output's
/// start/stop signal handlers, `SetSpeakerVolume` by the levels thread —
/// none of those are ever parsed from stdin.
#[derive(Debug, Clone, PartialEq)]
pub enum Command {
    Start,
    Pause,
    Quit,
    MuteSpeaker(usize),
    UnmuteSpeaker(usize),
    MuteMic(usize),
    UnmuteMic(usize),
    /// `configure <path>`: re-read the settings JSON at the given path and
    /// apply the diff. The path is the trimmed remainder of the line and may
    /// contain spaces; no quoting or escaping.
    Configure(String),
    /// OBS "start" signal fired — recording is actually rolling.
    OutputStarted,
    /// OBS "stop" signal fired with the given stop code (spontaneous or in
    /// response to our own `obs_output_stop`).
    OutputStopped(i64),
    /// Levels-thread volume-compensation update: set speaker `idx`'s source
    /// volume. Routed through the run loop so source pointers are only ever
    /// touched by the thread that owns their lifetime.
    SetSpeakerVolume(usize, f32),
}

/// Whitespace-split, first token dispatched case-insensitively. Unknown lines
/// return `None` (the caller logs and ignores them).
pub fn parse_command(line: &str) -> Option<Command> {
    let line = line.trim();
    let mut parts = line.split_whitespace();
    let head = parts.next()?.to_ascii_lowercase();
    if head == "configure" {
        // The argument is the rest of the line, not one token: paths may
        // contain spaces. ASCII lowercasing preserves byte length, so
        // `head.len()` is where the first token ends.
        let path = line[head.len()..].trim();
        return (!path.is_empty()).then(|| Command::Configure(path.to_string()));
    }
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

    #[test]
    fn parses_configure_with_the_rest_of_the_line_as_path() {
        assert_eq!(
            parse_command("configure /tmp/obs-settings.json"),
            Some(Command::Configure("/tmp/obs-settings.json".to_string()))
        );
        // Spaces inside the path are preserved; surrounding whitespace is not.
        assert_eq!(
            parse_command("  CONFIGURE  C:\\Users\\Jane Doe\\obs-settings.json  "),
            Some(Command::Configure(
                "C:\\Users\\Jane Doe\\obs-settings.json".to_string()
            ))
        );
        // No quote stripping: quotes are part of the path.
        assert_eq!(
            parse_command("configure \"/tmp/a.json\""),
            Some(Command::Configure("\"/tmp/a.json\"".to_string()))
        );
        // Bare `configure` is malformed.
        assert_eq!(parse_command("configure"), None);
        assert_eq!(parse_command("configure   "), None);
    }
}
