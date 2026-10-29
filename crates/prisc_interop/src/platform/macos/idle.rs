//! macOS: the system idle time (`prisc_idle_seconds`).

use objc2_core_graphics::{CGEventSource, CGEventSourceStateID, CGEventType};

use crate::error::Result;

/// `kCGAnyInputEventType`: every input event type.
const ANY_INPUT_EVENT: u32 = !0;

/// Seconds since the last input event of any type in the whole login session; `None` when
/// CoreGraphics answers a negative interval.
pub(crate) fn idle_seconds() -> Result<Option<f64>> {
    let seconds = CGEventSource::seconds_since_last_event_type(CGEventSourceStateID::CombinedSessionState, CGEventType(ANY_INPUT_EVENT));
    Ok((seconds >= 0.0).then_some(seconds))
}
