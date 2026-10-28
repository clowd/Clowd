//! The run loop's command queue. The stdin commands arrive as `recorder::Command`
//! (crates/prisc_capture/HELPER_PROTOCOL.md §4.1) and are mapped onto [`Command`] by
//! [`from_wire`]; everything else in the queue is injected from inside the process.

use prisc_ipc::catalog::recorder;
use prisc_ipc::Inbound;

/// Commands consumed by the recorder's run loop. Most originate from stdin;
/// `Quit` is posted by the quit path (`quit`, stdin EOF, a signal or a dead
/// stdout), `OutputStarted` / `OutputStopped` / `OutputDeactivated` by the
/// OBS output's start/stop/deactivate signal handlers, and `SetSpeakerVolume`
/// by the levels thread.
#[derive(Debug, Clone, PartialEq)]
pub enum Command {
    Start,
    Pause,
    Quit,
    MuteSpeaker(usize),
    UnmuteSpeaker(usize),
    MuteMic(usize),
    UnmuteMic(usize),
    /// Re-read the settings JSON at the given path and apply the diff.
    Configure(String),
    /// A stdin line that was not a command, answered with `bad_command` in
    /// its place in the queue so it cannot overtake an earlier command's ack.
    Malformed(String),
    /// OBS "start" signal fired: recording is actually rolling.
    OutputStarted,
    /// OBS "stop" signal fired with the given stop code (spontaneous or in
    /// response to our own `obs_output_stop`).
    OutputStopped(i64),
    /// OBS "deactivate" signal fired: the output has fully torn down its
    /// data capture. For mp4_output this is the earliest point at which the
    /// file is guaranteed flushed and closed: the "stop" signal fires *before*
    /// the buffered file serializer drains (see `Recorder::wait_for_flush`).
    OutputDeactivated,
    /// Levels-thread volume-compensation update: set speaker `idx`'s source
    /// volume. Routed through the run loop so source pointers are only ever
    /// touched by the thread that owns their lifetime.
    SetSpeakerVolume(usize, f32),
}

/// One stdin line, as the run loop sees it.
pub fn from_wire(inbound: Inbound<recorder::Command>) -> Command {
    let index = |i: u32| i as usize;
    match inbound {
        Inbound::Command(command) => match command {
            recorder::Command::Start {} => Command::Start,
            recorder::Command::Pause {} => Command::Pause,
            recorder::Command::MuteSpeaker { index: i } => Command::MuteSpeaker(index(i)),
            recorder::Command::UnmuteSpeaker { index: i } => Command::UnmuteSpeaker(index(i)),
            recorder::Command::MuteMic { index: i } => Command::MuteMic(index(i)),
            recorder::Command::UnmuteMic { index: i } => Command::UnmuteMic(index(i)),
            recorder::Command::Configure { path } => Command::Configure(path),
        },
        Inbound::Malformed { reason } => Command::Malformed(reason),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(line: &str) -> Command {
        from_wire(Inbound::Command(serde_json::from_str(line).unwrap()))
    }

    #[test]
    fn maps_every_wire_command() {
        assert_eq!(parse(r#"{"type":"start"}"#), Command::Start);
        assert_eq!(parse(r#"{"type":"pause"}"#), Command::Pause);
        assert_eq!(parse(r#"{"type":"mute_speaker","index":1}"#), Command::MuteSpeaker(1));
        assert_eq!(parse(r#"{"type":"unmute_speaker","index":0}"#), Command::UnmuteSpeaker(0));
        assert_eq!(parse(r#"{"type":"mute_mic","index":2}"#), Command::MuteMic(2));
        assert_eq!(parse(r#"{"type":"unmute_mic","index":0}"#), Command::UnmuteMic(0));
    }

    #[test]
    fn configure_paths_keep_spaces_and_quotes() {
        assert_eq!(
            parse(r#"{"type":"configure","path":"C:\\Users\\Jane Doe\\\"s\".json"}"#),
            Command::Configure("C:\\Users\\Jane Doe\\\"s\".json".to_string())
        );
    }

    #[test]
    fn malformed_lines_become_bad_commands() {
        assert_eq!(
            from_wire(Inbound::Malformed { reason: "unknown command 'mvoe'".into() }),
            Command::Malformed("unknown command 'mvoe'".into())
        );
    }
}
