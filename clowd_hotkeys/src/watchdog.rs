//! Detects a WH_KEYBOARD_LL hook that Windows removed silently (after repeated callback timeouts,
//! e.g. the host paused in a debugger for over a second; the docs say "there is no way for the
//! application to know"). Kept free of Win32 calls so it is tested on every OS; `windows.rs`
//! counts hook calls, feeds it one canary per raw-input key event and re-hooks on its verdict.

/// Canary key events in a row with no hook call in between before the hook is presumed gone. A
/// live hook sees every key event the canary sees, give or take the one in flight.
const SILENT_CANARIES: u32 = 3;
/// At most one re-hook per this long. While an elevated window has focus the hook is legitimately
/// not called, yet raw input may still arrive: re-hooking then is harmless but must not loop.
const REHOOK_INTERVAL_MS: u64 = 5000;

pub(crate) struct Watchdog {
    /// The hook call count at the last canary.
    calls: u64,
    /// Canaries in a row that saw `calls` unchanged.
    silent: u32,
    /// When the last re-hook was asked for.
    rehooked_at: Option<u64>,
}

impl Watchdog {
    pub fn new() -> Self {
        Self {
            calls: 0,
            silent: 0,
            rehooked_at: None,
        }
    }

    /// One canary key event. `calls` = hook invocations so far, `now_ms` = a monotonic clock.
    /// Returns true when the hook should be re-installed now. No canaries, no judgement: nothing
    /// typed is never mistaken for a dead hook.
    pub fn canary(&mut self, calls: u64, now_ms: u64) -> bool {
        if calls != self.calls {
            self.calls = calls;
            self.silent = 0;
            return false;
        }
        self.silent = self.silent.saturating_add(1);
        if self.silent < SILENT_CANARIES || matches!(self.rehooked_at, Some(at) if now_ms.saturating_sub(at) < REHOOK_INTERVAL_MS) {
            return false;
        }
        self.silent = 0;
        self.rehooked_at = Some(now_ms);
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn silent_canaries_in_a_row_ask_for_a_rehook() {
        let mut dog = Watchdog::new();
        assert!(!dog.canary(0, 0));
        assert!(!dog.canary(0, 1));
        assert!(dog.canary(0, 2));
        // The count starts over after a re-hook.
        assert!(!dog.canary(0, 10_000));
        assert!(!dog.canary(0, 10_001));
        assert!(dog.canary(0, 10_002));
    }

    #[test]
    fn any_hook_call_resets_the_count() {
        let mut dog = Watchdog::new();
        let mut calls = 0;
        for now in 0..100 {
            // A live hook: one call per key event, the canary either side of it.
            if now % 2 == 0 {
                calls += 1;
            }
            assert!(!dog.canary(calls, now));
        }
        assert!(!dog.canary(calls, 100));
        assert!(!dog.canary(calls + 1, 101));
        assert!(!dog.canary(calls + 1, 102));
        assert!(!dog.canary(calls + 1, 103));
        assert!(dog.canary(calls + 1, 104));
    }

    #[test]
    fn hook_calls_without_canaries_judge_nothing() {
        let mut dog = Watchdog::new();
        // Nothing typed for an hour, or only hook calls (injected keys raw input never sees).
        assert!(!dog.canary(1_000, 3_600_000));
        assert!(!dog.canary(1_000, 3_600_001));
        assert!(!dog.canary(5_000, 3_600_002));
    }

    #[test]
    fn rehooks_are_rate_limited() {
        let mut dog = Watchdog::new();
        assert!(!dog.canary(0, 1_000));
        assert!(!dog.canary(0, 1_001));
        assert!(dog.canary(0, 1_002));
        // Still silent (an elevated window has focus): no re-hook until the interval has passed,
        // then the very next canary re-hooks.
        for now in 1_003..1_002 + REHOOK_INTERVAL_MS {
            assert!(!dog.canary(0, now));
        }
        assert!(dog.canary(0, 1_002 + REHOOK_INTERVAL_MS));
        // A hook call in between resets the silence even while rate limited.
        assert!(!dog.canary(0, 20_000));
        assert!(!dog.canary(0, 20_001));
        assert!(!dog.canary(1, 20_002));
        assert!(!dog.canary(1, 20_003));
        assert!(!dog.canary(1, 20_004));
        assert!(dog.canary(1, 20_005));
    }
}
