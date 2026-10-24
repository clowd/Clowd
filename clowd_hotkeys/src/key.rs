//! The key table: every key a global hotkey can name, with its Avalonia value, its alias names
//! and its platform codes, on one row per key. This is the only global-hotkey key map in Clowd —
//! the C# `HotkeyKey` mirror is generated from it (`csharp.rs`), and the backends map OS codes
//! through it.

/// `Some(first code)` or `None` for an empty code list.
macro_rules! first {
    () => {
        None
    };
    ($first:literal $(, $rest:literal)*) => {
        Some($first)
    };
}

macro_rules! keys {
    ($($name:ident = $value:literal, [$($alias:literal),*], win[$($vk:literal),*], mac[$($mac:literal),*], nx[$($nx:literal),*];)*) => {
        /// A key a global hotkey can name. The variant names are exactly what .NET printed for
        /// Avalonia 12.1.2's `Key` and the values are Avalonia's, so settings saved by older builds
        /// (and the generated C# `HotkeyKey`) line up one to one. `None` means "no key / unmapped".
        #[repr(i32)]
        #[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
        pub enum Key {
            $($name = $value,)*
        }

        impl Key {
            /// Every key, in declaration order (= ascending value).
            pub(crate) const ALL: &'static [Key] = &[$(Key::$name),*];

            /// The canonical name: what settings store and `Gesture` prints.
            pub fn name(self) -> &'static str {
                match self {
                    $(Key::$name => stringify!($name),)*
                }
            }

            /// Other names that parse to this key (Avalonia's duplicate enum names, e.g. `Snapshot`).
            pub(crate) fn aliases(self) -> &'static [&'static str] {
                match self {
                    $(Key::$name => &[$($alias),*],)*
                }
            }

            /// The key with this exact value, if one is defined.
            pub fn from_i32(value: i32) -> Option<Key> {
                match value {
                    $($value => Some(Key::$name),)*
                    _ => None,
                }
            }

            /// The canonical Windows virtual-key code.
            #[cfg_attr(not(windows), allow(dead_code))]
            pub(crate) fn to_vk(self) -> Option<u16> {
                match self {
                    $(Key::$name => first!($($vk),*),)*
                }
            }

            /// The key for a Windows virtual-key code (every listed code, not just the canonical one).
            pub(crate) fn from_vk(vk: u16) -> Option<Key> {
                match vk {
                    $($($vk => Some(Key::$name),)*)*
                    _ => None,
                }
            }

            /// The canonical macOS CGKeyCode.
            #[cfg_attr(not(target_os = "macos"), allow(dead_code))]
            pub(crate) fn to_mac(self) -> Option<u16> {
                match self {
                    $(Key::$name => first!($($mac),*),)*
                }
            }

            /// The key for a macOS CGKeyCode (every listed code).
            #[cfg_attr(not(target_os = "macos"), allow(dead_code))]
            pub(crate) fn from_mac(code: u16) -> Option<Key> {
                match code {
                    $($($mac => Some(Key::$name),)*)*
                    _ => None,
                }
            }

            /// The canonical macOS NX_KEYTYPE (system-defined media event) code.
            #[cfg_attr(not(target_os = "macos"), allow(dead_code))]
            pub(crate) fn to_nx(self) -> Option<u32> {
                match self {
                    $(Key::$name => first!($($nx),*),)*
                }
            }

            /// The key for a macOS NX_KEYTYPE code (every listed code).
            #[cfg_attr(not(target_os = "macos"), allow(dead_code))]
            pub(crate) fn from_nx(keytype: u32) -> Option<Key> {
                match keytype {
                    $($($nx => Some(Key::$name),)*)*
                    _ => None,
                }
            }
        }
    };
}

// A code listed twice in one column becomes an unreachable pattern in from_vk / from_mac /
// from_nx, which `clippy -D warnings` rejects: that is the duplicate guard.
// Windows maps by VK like Avalonia's Win32 backend (layout-dependent; LeftShift/LeftCtrl/LeftAlt
// also take the generic VK_SHIFT/VK_CONTROL/VK_MENU that injected events can carry; numpad keys
// with NumLock off arrive as navigation VKs). macOS maps by CGKeyCode positionally (ANSI names)
// like Avalonia.Native, keypad Enter included as Return. NX is NSSystemDefined subtype 8
// `data1 >> 16`.
keys! {
    // Name                        =  value, [aliases], win[VK...], mac[CGKeyCode...], nx[NX_KEYTYPE...];
    None                           =      0, [], win[], mac[], nx[];
    Cancel                         =      1, [], win[0x03], mac[], nx[];
    Back                           =      2, [], win[0x08], mac[0x33], nx[];
    Tab                            =      3, [], win[0x09], mac[0x30], nx[];
    LineFeed                       =      4, [], win[], mac[], nx[];
    Clear                          =      5, [], win[0x0C], mac[0x47], nx[];
    Return                         =      6, ["Enter"], win[0x0D], mac[0x24, 0x4C], nx[];
    Pause                          =      7, [], win[0x13], mac[], nx[];
    CapsLock                       =      8, ["Capital"], win[0x14], mac[0x39], nx[];
    HangulMode                     =      9, ["KanaMode"], win[0x15], mac[0x68], nx[];
    JunjaMode                      =     10, [], win[0x17], mac[], nx[];
    FinalMode                      =     11, [], win[0x18], mac[], nx[];
    HanjaMode                      =     12, ["KanjiMode"], win[0x19], mac[], nx[];
    Escape                         =     13, [], win[0x1B], mac[0x35], nx[];
    ImeConvert                     =     14, [], win[0x1C], mac[], nx[];
    ImeNonConvert                  =     15, [], win[0x1D], mac[], nx[];
    ImeAccept                      =     16, [], win[0x1E], mac[], nx[];
    ImeModeChange                  =     17, [], win[0x1F], mac[], nx[];
    Space                          =     18, [], win[0x20], mac[0x31], nx[];
    PageUp                         =     19, ["Prior"], win[0x21], mac[0x74], nx[];
    PageDown                       =     20, ["Next"], win[0x22], mac[0x79], nx[];
    End                            =     21, [], win[0x23], mac[0x77], nx[];
    Home                           =     22, [], win[0x24], mac[0x73], nx[];
    Left                           =     23, [], win[0x25], mac[0x7B], nx[];
    Up                             =     24, [], win[0x26], mac[0x7E], nx[];
    Right                          =     25, [], win[0x27], mac[0x7C], nx[];
    Down                           =     26, [], win[0x28], mac[0x7D], nx[];
    Select                         =     27, [], win[0x29], mac[], nx[];
    Print                          =     28, [], win[0x2A], mac[], nx[];
    Execute                        =     29, [], win[0x2B], mac[], nx[];
    PrintScreen                    =     30, ["Snapshot"], win[0x2C], mac[], nx[];
    Insert                         =     31, [], win[0x2D], mac[0x72], nx[];
    Delete                         =     32, [], win[0x2E], mac[0x75], nx[];
    Help                           =     33, [], win[0x2F], mac[], nx[];
    D0                             =     34, [], win[0x30], mac[0x1D], nx[];
    D1                             =     35, [], win[0x31], mac[0x12], nx[];
    D2                             =     36, [], win[0x32], mac[0x13], nx[];
    D3                             =     37, [], win[0x33], mac[0x14], nx[];
    D4                             =     38, [], win[0x34], mac[0x15], nx[];
    D5                             =     39, [], win[0x35], mac[0x17], nx[];
    D6                             =     40, [], win[0x36], mac[0x16], nx[];
    D7                             =     41, [], win[0x37], mac[0x1A], nx[];
    D8                             =     42, [], win[0x38], mac[0x1C], nx[];
    D9                             =     43, [], win[0x39], mac[0x19], nx[];
    A                              =     44, [], win[0x41], mac[0x00], nx[];
    B                              =     45, [], win[0x42], mac[0x0B], nx[];
    C                              =     46, [], win[0x43], mac[0x08], nx[];
    D                              =     47, [], win[0x44], mac[0x02], nx[];
    E                              =     48, [], win[0x45], mac[0x0E], nx[];
    F                              =     49, [], win[0x46], mac[0x03], nx[];
    G                              =     50, [], win[0x47], mac[0x05], nx[];
    H                              =     51, [], win[0x48], mac[0x04], nx[];
    I                              =     52, [], win[0x49], mac[0x22], nx[];
    J                              =     53, [], win[0x4A], mac[0x26], nx[];
    K                              =     54, [], win[0x4B], mac[0x28], nx[];
    L                              =     55, [], win[0x4C], mac[0x25], nx[];
    M                              =     56, [], win[0x4D], mac[0x2E], nx[];
    N                              =     57, [], win[0x4E], mac[0x2D], nx[];
    O                              =     58, [], win[0x4F], mac[0x1F], nx[];
    P                              =     59, [], win[0x50], mac[0x23], nx[];
    Q                              =     60, [], win[0x51], mac[0x0C], nx[];
    R                              =     61, [], win[0x52], mac[0x0F], nx[];
    S                              =     62, [], win[0x53], mac[0x01], nx[];
    T                              =     63, [], win[0x54], mac[0x11], nx[];
    U                              =     64, [], win[0x55], mac[0x20], nx[];
    V                              =     65, [], win[0x56], mac[0x09], nx[];
    W                              =     66, [], win[0x57], mac[0x0D], nx[];
    X                              =     67, [], win[0x58], mac[0x07], nx[];
    Y                              =     68, [], win[0x59], mac[0x10], nx[];
    Z                              =     69, [], win[0x5A], mac[0x06], nx[];
    LWin                           =     70, [], win[0x5B], mac[0x37], nx[];
    RWin                           =     71, [], win[0x5C], mac[0x36], nx[];
    Apps                           =     72, [], win[0x5D], mac[0x6E], nx[];
    Sleep                          =     73, [], win[0x5F], mac[], nx[];
    NumPad0                        =     74, [], win[0x60], mac[0x52], nx[];
    NumPad1                        =     75, [], win[0x61], mac[0x53], nx[];
    NumPad2                        =     76, [], win[0x62], mac[0x54], nx[];
    NumPad3                        =     77, [], win[0x63], mac[0x55], nx[];
    NumPad4                        =     78, [], win[0x64], mac[0x56], nx[];
    NumPad5                        =     79, [], win[0x65], mac[0x57], nx[];
    NumPad6                        =     80, [], win[0x66], mac[0x58], nx[];
    NumPad7                        =     81, [], win[0x67], mac[0x59], nx[];
    NumPad8                        =     82, [], win[0x68], mac[0x5B], nx[];
    NumPad9                        =     83, [], win[0x69], mac[0x5C], nx[];
    Multiply                       =     84, [], win[0x6A], mac[0x43], nx[];
    Add                            =     85, [], win[0x6B], mac[0x45], nx[];
    Separator                      =     86, [], win[0x6C], mac[], nx[];
    Subtract                       =     87, [], win[0x6D], mac[0x4E], nx[];
    Decimal                        =     88, [], win[0x6E], mac[0x41], nx[];
    Divide                         =     89, [], win[0x6F], mac[0x4B], nx[];
    F1                             =     90, [], win[0x70], mac[0x7A], nx[];
    F2                             =     91, [], win[0x71], mac[0x78], nx[];
    F3                             =     92, [], win[0x72], mac[0x63], nx[];
    F4                             =     93, [], win[0x73], mac[0x76], nx[];
    F5                             =     94, [], win[0x74], mac[0x60], nx[];
    F6                             =     95, [], win[0x75], mac[0x61], nx[];
    F7                             =     96, [], win[0x76], mac[0x62], nx[];
    F8                             =     97, [], win[0x77], mac[0x64], nx[];
    F9                             =     98, [], win[0x78], mac[0x65], nx[];
    F10                            =     99, [], win[0x79], mac[0x6D], nx[];
    F11                            =    100, [], win[0x7A], mac[0x67], nx[];
    F12                            =    101, [], win[0x7B], mac[0x6F], nx[];
    F13                            =    102, [], win[0x7C], mac[0x69], nx[];
    F14                            =    103, [], win[0x7D], mac[0x6B], nx[];
    F15                            =    104, [], win[0x7E], mac[0x71], nx[];
    F16                            =    105, [], win[0x7F], mac[0x6A], nx[];
    F17                            =    106, [], win[0x80], mac[0x40], nx[];
    F18                            =    107, [], win[0x81], mac[0x4F], nx[];
    F19                            =    108, [], win[0x82], mac[0x50], nx[];
    F20                            =    109, [], win[0x83], mac[0x5A], nx[];
    F21                            =    110, [], win[0x84], mac[], nx[];
    F22                            =    111, [], win[0x85], mac[], nx[];
    F23                            =    112, [], win[0x86], mac[], nx[];
    F24                            =    113, [], win[0x87], mac[], nx[];
    NumLock                        =    114, [], win[0x90], mac[], nx[];
    Scroll                         =    115, [], win[0x91], mac[], nx[];
    LeftShift                      =    116, [], win[0xA0, 0x10], mac[0x38], nx[];
    RightShift                     =    117, [], win[0xA1], mac[0x3C], nx[];
    LeftCtrl                       =    118, [], win[0xA2, 0x11], mac[0x3B], nx[];
    RightCtrl                      =    119, [], win[0xA3], mac[0x3E], nx[];
    LeftAlt                        =    120, [], win[0xA4, 0x12], mac[0x3A], nx[];
    RightAlt                       =    121, [], win[0xA5], mac[0x3D], nx[];
    BrowserBack                    =    122, [], win[0xA6], mac[], nx[];
    BrowserForward                 =    123, [], win[0xA7], mac[], nx[];
    BrowserRefresh                 =    124, [], win[0xA8], mac[], nx[];
    BrowserStop                    =    125, [], win[0xA9], mac[], nx[];
    BrowserSearch                  =    126, [], win[0xAA], mac[], nx[];
    BrowserFavorites               =    127, [], win[0xAB], mac[], nx[];
    BrowserHome                    =    128, [], win[0xAC], mac[], nx[];
    VolumeMute                     =    129, [], win[0xAD], mac[0x4A], nx[7];
    VolumeDown                     =    130, [], win[0xAE], mac[0x49], nx[1];
    VolumeUp                       =    131, [], win[0xAF], mac[0x48], nx[0];
    MediaNextTrack                 =    132, [], win[0xB0], mac[], nx[17, 19];
    MediaPreviousTrack             =    133, [], win[0xB1], mac[], nx[18, 20];
    MediaStop                      =    134, [], win[0xB2], mac[], nx[];
    MediaPlayPause                 =    135, [], win[0xB3], mac[], nx[16];
    LaunchMail                     =    136, [], win[0xB4], mac[], nx[];
    SelectMedia                    =    137, [], win[0xB5], mac[], nx[];
    LaunchApplication1             =    138, [], win[0xB6], mac[], nx[];
    LaunchApplication2             =    139, [], win[0xB7], mac[], nx[];
    OemSemicolon                   =    140, ["Oem1"], win[0xBA], mac[0x29], nx[];
    OemPlus                        =    141, [], win[0xBB], mac[0x18], nx[];
    OemComma                       =    142, [], win[0xBC], mac[0x2B], nx[];
    OemMinus                       =    143, [], win[0xBD], mac[0x1B], nx[];
    OemPeriod                      =    144, [], win[0xBE], mac[0x2F], nx[];
    OemQuestion                    =    145, ["Oem2"], win[0xBF], mac[0x2C], nx[];
    Oem3                           =    146, ["OemTilde"], win[0xC0], mac[0x32], nx[];
    AbntC1                         =    147, [], win[0xC1], mac[], nx[];
    AbntC2                         =    148, [], win[0xC2], mac[], nx[];
    Oem4                           =    149, ["OemOpenBrackets"], win[0xDB], mac[0x21], nx[];
    OemPipe                        =    150, ["Oem5"], win[0xDC], mac[0x2A], nx[];
    OemCloseBrackets               =    151, ["Oem6"], win[0xDD], mac[0x1E], nx[];
    OemQuotes                      =    152, ["Oem7"], win[0xDE], mac[0x27], nx[];
    Oem8                           =    153, [], win[0xDF], mac[], nx[];
    OemBackslash                   =    154, ["Oem102"], win[0xE2], mac[0x0A], nx[];
    ImeProcessed                   =    155, [], win[0xE5], mac[], nx[];
    System                         =    156, [], win[], mac[], nx[];
    OemAttn                        =    157, ["DbeAlphanumeric"], win[0xF0], mac[], nx[];
    OemFinish                      =    158, ["DbeKatakana"], win[0xF1], mac[], nx[];
    OemCopy                        =    159, ["DbeHiragana"], win[0xF2], mac[], nx[];
    OemAuto                        =    160, ["DbeSbcsChar"], win[0xF3], mac[], nx[];
    OemEnlw                        =    161, ["DbeDbcsChar"], win[0xF4], mac[], nx[];
    OemBackTab                     =    162, ["DbeRoman"], win[0xF5], mac[], nx[];
    DbeNoRoman                     =    163, ["Attn"], win[0xF6], mac[], nx[];
    CrSel                          =    164, ["DbeEnterWordRegisterMode"], win[0xF7], mac[], nx[];
    ExSel                          =    165, ["DbeEnterImeConfigureMode"], win[0xF8], mac[], nx[];
    DbeFlushString                 =    166, ["EraseEof"], win[0xF9], mac[], nx[];
    DbeCodeInput                   =    167, ["Play"], win[0xFA], mac[], nx[];
    Zoom                           =    168, ["DbeNoCodeInput"], win[0xFB], mac[], nx[];
    NoName                         =    169, ["DbeDetermineString"], win[0xFC], mac[], nx[];
    DbeEnterDialogConversionMode   =    170, ["Pa1"], win[0xFD], mac[], nx[];
    OemClear                       =    171, [], win[0xFE], mac[], nx[];
    DeadCharProcessed              =    172, [], win[], mac[], nx[];
    FnLeftArrow                    =  10001, [], win[], mac[], nx[];
    FnRightArrow                   =  10002, [], win[], mac[], nx[];
    FnUpArrow                      =  10003, [], win[], mac[], nx[];
    FnDownArrow                    =  10004, [], win[], mac[], nx[];
    MediaHome                      = 100000, [], win[], mac[], nx[];
    MediaChannelList               = 100001, [], win[], mac[], nx[];
    MediaChannelRaise              = 100002, [], win[], mac[], nx[];
    MediaChannelLower              = 100003, [], win[], mac[], nx[];
    MediaRecord                    = 100005, [], win[], mac[], nx[];
    MediaRed                       = 100010, [], win[], mac[], nx[];
    MediaGreen                     = 100011, [], win[], mac[], nx[];
    MediaYellow                    = 100012, [], win[], mac[], nx[];
    MediaBlue                      = 100013, [], win[], mac[], nx[];
    MediaMenu                      = 100020, [], win[], mac[], nx[];
    MediaMore                      = 100021, [], win[], mac[], nx[];
    MediaOption                    = 100022, [], win[], mac[], nx[];
    MediaInfo                      = 100023, [], win[], mac[], nx[];
    MediaSearch                    = 100024, [], win[], mac[], nx[];
    MediaSubtitle                  = 100025, [], win[], mac[], nx[];
    MediaTvGuide                   = 100026, [], win[], mac[], nx[];
    MediaPreviousChannel           = 100027, [], win[], mac[], nx[];
}

impl Key {
    /// The key with this canonical name, alias or label: trimmed, ASCII case-insensitive. Numbers
    /// other than the top-row digits' labels and comma lists (.NET `Enum.TryParse` accidents) are
    /// not names.
    pub fn from_name(name: &str) -> Option<Key> {
        let name = name.trim();
        Key::ALL.iter().copied().find(|key| {
            key.name().eq_ignore_ascii_case(name)
                || key.label().eq_ignore_ascii_case(name)
                || key
                    .aliases()
                    .iter()
                    .any(|alias| alias.eq_ignore_ascii_case(name))
        })
    }

    /// The short display name ("PrtScr", "Del", "1", "Num7", "=") — what labels show, never what
    /// settings store. Parses back like any name. Keys without a shorter label show their name.
    pub fn label(self) -> &'static str {
        match self {
            Key::Back => "Backspace",
            Key::Return => "Enter",
            Key::Escape => "Esc",
            Key::PageUp => "PgUp",
            Key::PageDown => "PgDn",
            Key::PrintScreen => "PrtScr",
            Key::Insert => "Ins",
            Key::Delete => "Del",
            Key::D0 => "0",
            Key::D1 => "1",
            Key::D2 => "2",
            Key::D3 => "3",
            Key::D4 => "4",
            Key::D5 => "5",
            Key::D6 => "6",
            Key::D7 => "7",
            Key::D8 => "8",
            Key::D9 => "9",
            Key::Apps => "Menu",
            Key::NumPad0 => "Num0",
            Key::NumPad1 => "Num1",
            Key::NumPad2 => "Num2",
            Key::NumPad3 => "Num3",
            Key::NumPad4 => "Num4",
            Key::NumPad5 => "Num5",
            Key::NumPad6 => "Num6",
            Key::NumPad7 => "Num7",
            Key::NumPad8 => "Num8",
            Key::NumPad9 => "Num9",
            Key::Multiply => "Num*",
            // the one label holding '+': `Gesture::from_str` takes a trailing "Num+" as this key
            Key::Add => "Num+",
            Key::Subtract => "Num-",
            Key::Decimal => "Num.",
            Key::Divide => "Num/",
            Key::NumLock => "NumLk",
            Key::Scroll => "ScrLk",
            Key::VolumeMute => "Mute",
            Key::VolumeDown => "Vol Down",
            Key::VolumeUp => "Vol Up",
            Key::MediaNextTrack => "Next Track",
            Key::MediaPreviousTrack => "Prev Track",
            Key::MediaStop => "Stop",
            Key::MediaPlayPause => "Play/Pause",
            Key::OemSemicolon => ";",
            Key::OemPlus => "=",
            Key::OemComma => ",",
            Key::OemMinus => "-",
            Key::OemPeriod => ".",
            Key::OemQuestion => "/",
            Key::Oem3 => "`",
            Key::Oem4 => "[",
            Key::OemPipe => "\\",
            Key::OemCloseBrackets => "]",
            Key::OemQuotes => "'",
            _ => self.name(),
        }
    }

    /// A modifier key (either side): these combine into `Modifiers` and are never a hotkey's key.
    pub fn is_modifier(self) -> bool {
        matches!(
            self,
            Key::LWin | Key::RWin | Key::LeftShift | Key::RightShift | Key::LeftCtrl | Key::RightCtrl | Key::LeftAlt | Key::RightAlt
        )
    }
}

impl std::fmt::Display for Key {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.name())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn table_is_ascending_and_complete() {
        assert_eq!(Key::ALL.len(), 194);
        assert!(Key::ALL
            .windows(2)
            .all(|w| (w[0] as i32) < (w[1] as i32)));
        for &key in Key::ALL {
            assert_eq!(Key::from_i32(key as i32), Some(key));
        }
        assert_eq!(Key::from_i32(-1), None);
        assert_eq!(Key::from_i32(100004), None);
        assert_eq!(
            Key::ALL
                .iter()
                .filter(|k| k.to_vk().is_some())
                .count(),
            169
        );
        assert_eq!(
            Key::ALL
                .iter()
                .filter(|k| k.to_mac().is_some())
                .count(),
            113
        );
    }

    #[test]
    fn names_aliases_and_labels_are_unique_ignoring_case() {
        // The generated C# dictionary is OrdinalIgnoreCase; a clash would throw at type init.
        let mut seen = std::collections::HashMap::new();
        for &key in Key::ALL {
            for name in [key.name(), key.label()]
                .into_iter()
                .chain(key.aliases().iter().copied())
            {
                // a label may repeat its own key's name or alias (Return's "Enter")
                let owner = *seen
                    .entry(name.to_ascii_lowercase())
                    .or_insert(key);
                assert_eq!(owner, key, "{name} names both {owner:?} and {key:?}");
            }
        }
        assert_eq!(seen.len(), 276);
    }

    #[test]
    fn labels_parse_back() {
        for &key in Key::ALL {
            assert_eq!(Key::from_name(key.label()), Some(key), "{key:?}");
            // '+' separates gesture parts; only Add's "Num+" is let through (see Gesture::from_str)
            assert!(key == Key::Add || !key.label().contains('+'), "{key:?}");
        }
        assert_eq!(Key::PrintScreen.label(), "PrtScr");
        assert_eq!(Key::D1.label(), "1");
        assert_eq!(Key::A.label(), "A");
        assert_eq!(Key::F12.label(), "F12");
    }

    #[test]
    fn from_name_is_tolerant_but_not_numeric() {
        assert_eq!(Key::from_name("PrintScreen"), Some(Key::PrintScreen));
        assert_eq!(Key::from_name(" snapshot "), Some(Key::PrintScreen));
        assert_eq!(Key::from_name("prtscr"), Some(Key::PrintScreen));
        assert_eq!(Key::from_name("ENTER"), Some(Key::Return));
        assert_eq!(Key::from_name("oemtilde"), Some(Key::Oem3));
        assert_eq!(Key::from_name("`"), Some(Key::Oem3));
        assert_eq!(Key::from_name("7"), Some(Key::D7));
        assert_eq!(Key::from_name("None"), Some(Key::None));
        for bad in ["", " ", "30", "-1", "A,B", "Ctrl", "Shift", "NotAKey"] {
            assert_eq!(Key::from_name(bad), None, "{bad}");
        }
    }

    #[test]
    fn platform_codes_round_trip() {
        for &key in Key::ALL {
            if let Some(vk) = key.to_vk() {
                assert_eq!(Key::from_vk(vk), Some(key));
            }
            if let Some(code) = key.to_mac() {
                assert_eq!(Key::from_mac(code), Some(key));
            }
            if let Some(nx) = key.to_nx() {
                assert_eq!(Key::from_nx(nx), Some(key));
            }
        }
        // Secondary codes map back to their row too.
        assert_eq!(Key::from_vk(0x10), Some(Key::LeftShift));
        assert_eq!(Key::from_vk(0x11), Some(Key::LeftCtrl));
        assert_eq!(Key::from_vk(0x12), Some(Key::LeftAlt));
        assert_eq!(Key::from_mac(0x4C), Some(Key::Return));
        assert_eq!(Key::from_nx(19), Some(Key::MediaNextTrack));
        assert_eq!(Key::from_nx(20), Some(Key::MediaPreviousTrack));
        assert_eq!(Key::from_vk(0xE8), None);
        assert_eq!(Key::from_mac(0x3F), None);
    }

    #[test]
    fn modifiers_are_the_eight_side_keys() {
        let modifiers: Vec<Key> = Key::ALL
            .iter()
            .copied()
            .filter(|k| k.is_modifier())
            .collect();
        assert_eq!(
            modifiers,
            [
                Key::LWin,
                Key::RWin,
                Key::LeftShift,
                Key::RightShift,
                Key::LeftCtrl,
                Key::RightCtrl,
                Key::LeftAlt,
                Key::RightAlt
            ]
        );
        assert!(!Key::CapsLock.is_modifier());
        assert_eq!(Key::Oem3.to_string(), "Oem3");
    }
}
