//! The macOS overlay window level and collection behaviour, written once.
//!
//! Every Prisc window that must sit over the menu bar, fullscreen apps and every Space (the
//! capture overlay, the share-region border and toolbar, Draw on Screen, the floating tray)
//! uses the same level and collection behaviour; before this module the capturer, the share
//! region helper and Prisc.Ui each spelled the numbers themselves.
//!
//! - [`OVERLAY_LEVEL`] (25, `NSStatusWindowLevel`): one above `NSMainMenuWindowLevel` (24).
//!   AppKit constrains the frame of any window below the menu bar level so it never overlaps the
//!   bar; at this level that constraint is lifted.
//! - [`ABOVE_OVERLAY_LEVEL`] (26): one above that, for a window that must stay clickable over an
//!   overlay window which can itself become key (a window that becomes key orders itself to the
//!   front of its own level, so a same-level toolbar would sink on the first click).
//! - The collection behaviour: `Stationary | CanJoinAllSpaces | FullScreenAuxiliary |
//!   IgnoresCycle` ([`OverlayFlags::OVERLAY`]); callers that want a subset pass their own flags.
//!
//! The level constants and the flags exist on every OS so callers carry no cfg; [`apply`] and
//! [`raise_above_overlays`] exist only on macOS, where they take the `NSWindow` itself and so
//! inherit its main-thread rule.

/// `NSStatusWindowLevel`: the level overlay windows use so they can cover the menu bar.
pub const OVERLAY_LEVEL: isize = 25;

/// One above [`OVERLAY_LEVEL`], for a window that must stay above an overlay that can become key.
pub const ABOVE_OVERLAY_LEVEL: isize = OVERLAY_LEVEL + 1;

/// What [`apply`] sets on a window. The level bit sets [`OVERLAY_LEVEL`]; the other bits are
/// `NSWindowCollectionBehavior` members, and when any of them is present the window's collection
/// behaviour becomes exactly their union (it is replaced, not merged).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default)]
pub struct OverlayFlags(u32);

impl OverlayFlags {
    pub const NONE: Self = Self(0);
    /// `setLevel:` [`OVERLAY_LEVEL`].
    pub const LEVEL: Self = Self(1 << 0);
    /// `NSWindowCollectionBehaviorStationary`: unaffected by the Spaces switch animation.
    pub const STATIONARY: Self = Self(1 << 1);
    /// `NSWindowCollectionBehaviorCanJoinAllSpaces`: follows the user to every Space.
    pub const CAN_JOIN_ALL_SPACES: Self = Self(1 << 2);
    /// `NSWindowCollectionBehaviorFullScreenAuxiliary`: may sit over a fullscreen app.
    pub const FULL_SCREEN_AUXILIARY: Self = Self(1 << 3);
    /// `NSWindowCollectionBehaviorIgnoresCycle`: not in the Cmd+` window cycle.
    pub const IGNORES_CYCLE: Self = Self(1 << 4);

    /// Everything an overlay window gets: the level and the four collection behaviours.
    pub const OVERLAY: Self =
        Self(Self::LEVEL.0 | Self::STATIONARY.0 | Self::CAN_JOIN_ALL_SPACES.0 | Self::FULL_SCREEN_AUXILIARY.0 | Self::IGNORES_CYCLE.0);

    const COLLECTION_MASK: u32 = Self::STATIONARY.0 | Self::CAN_JOIN_ALL_SPACES.0 | Self::FULL_SCREEN_AUXILIARY.0 | Self::IGNORES_CYCLE.0;

    pub const fn bits(self) -> u32 {
        self.0
    }

    /// The flags for these bits, or `None` when an unknown bit is set.
    pub const fn from_bits(bits: u32) -> Option<Self> {
        if bits & !Self::OVERLAY.0 != 0 {
            None
        } else {
            Some(Self(bits))
        }
    }

    pub const fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }

    pub const fn union(self, other: Self) -> Self {
        Self(self.0 | other.0)
    }

    /// Whether any collection-behaviour bit is set.
    pub const fn sets_collection_behavior(self) -> bool {
        self.0 & Self::COLLECTION_MASK != 0
    }
}

impl std::ops::BitOr for OverlayFlags {
    type Output = Self;

    fn bitor(self, other: Self) -> Self {
        self.union(other)
    }
}

/// The `NSWindowCollectionBehavior` raw value for the collection bits of `flags` (0 when none).
/// Values: Stationary 1 << 4, CanJoinAllSpaces 1 << 0, FullScreenAuxiliary 1 << 8,
/// IgnoresCycle 1 << 6 (AppKit's own numbering, which C# used to hand-copy).
pub const fn collection_behavior_bits(flags: OverlayFlags) -> usize {
    let mut bits = 0usize;
    if flags.contains(OverlayFlags::CAN_JOIN_ALL_SPACES) {
        bits |= 1 << 0;
    }
    if flags.contains(OverlayFlags::STATIONARY) {
        bits |= 1 << 4;
    }
    if flags.contains(OverlayFlags::IGNORES_CYCLE) {
        bits |= 1 << 6;
    }
    if flags.contains(OverlayFlags::FULL_SCREEN_AUXILIARY) {
        bits |= 1 << 8;
    }
    bits
}

#[cfg(target_os = "macos")]
pub use mac::{apply, raise_above_overlays};

#[cfg(target_os = "macos")]
mod mac {
    use super::{collection_behavior_bits, OverlayFlags, ABOVE_OVERLAY_LEVEL, OVERLAY_LEVEL};
    use objc2_app_kit::{NSWindow, NSWindowCollectionBehavior};

    /// Sets the overlay level and/or collection behaviour `flags` asks for on `window`. Main
    /// thread (it is an AppKit call on the window).
    pub fn apply(window: &NSWindow, flags: OverlayFlags) {
        if flags.contains(OverlayFlags::LEVEL) {
            window.setLevel(OVERLAY_LEVEL);
        }
        if flags.sets_collection_behavior() {
            window.setCollectionBehavior(NSWindowCollectionBehavior(collection_behavior_bits(flags)));
        }
    }

    /// Lifts `window` to [`ABOVE_OVERLAY_LEVEL`] and orders it front without activating the app
    /// or making the window key (`orderFrontRegardless`). Main thread.
    pub fn raise_above_overlays(window: &NSWindow) {
        window.setLevel(ABOVE_OVERLAY_LEVEL);
        window.orderFrontRegardless();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn levels_are_the_status_level_and_one_above() {
        assert_eq!(OVERLAY_LEVEL, 25);
        assert_eq!(ABOVE_OVERLAY_LEVEL, 26);
    }

    #[test]
    fn overlay_collection_behavior_is_what_csharp_hand_copied() {
        // CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary, the value
        // WindowNativeExtensions.OverlayCollectionBehavior spelled by hand.
        assert_eq!(
            collection_behavior_bits(OverlayFlags::OVERLAY),
            (1 << 0) | (1 << 4) | (1 << 6) | (1 << 8)
        );
        assert!(OverlayFlags::OVERLAY.contains(OverlayFlags::LEVEL));
        assert!(OverlayFlags::OVERLAY.sets_collection_behavior());
        assert!(!OverlayFlags::LEVEL.sets_collection_behavior());
        assert_eq!(collection_behavior_bits(OverlayFlags::LEVEL), 0);
    }

    #[test]
    fn a_subset_renders_only_its_bits() {
        let share = OverlayFlags::CAN_JOIN_ALL_SPACES | OverlayFlags::STATIONARY;
        assert_eq!(collection_behavior_bits(share), (1 << 0) | (1 << 4));
        assert!(!share.contains(OverlayFlags::LEVEL));
    }

    #[test]
    fn unknown_bits_are_refused() {
        assert_eq!(OverlayFlags::from_bits(OverlayFlags::OVERLAY.bits()), Some(OverlayFlags::OVERLAY));
        assert_eq!(OverlayFlags::from_bits(1 << 5), None);
        assert_eq!(OverlayFlags::from_bits(0), Some(OverlayFlags::NONE));
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn the_appkit_values_match_the_crate_constants() {
        use objc2_app_kit::NSWindowCollectionBehavior as B;
        assert_eq!(
            collection_behavior_bits(OverlayFlags::OVERLAY),
            (B::Stationary | B::CanJoinAllSpaces | B::FullScreenAuxiliary | B::IgnoresCycle).0
        );
    }
}
