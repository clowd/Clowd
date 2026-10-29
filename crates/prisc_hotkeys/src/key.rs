//! The key table: every key a global hotkey can name, with its Avalonia value, its alias names
//! and its platform codes, on one row per key. This is the only global-hotkey key map in Prisc:
//! the C# `HotkeyKey` mirror is generated from it (`crates/prisc_codegen/src/hotkey_keys.rs`,
//! `cargo codegen`), and the backends map OS codes through it. The keystroke overlay's keycap
//! legends per OS key code are generated from it too (`KeycapLegends.g.cs`).

use crate::modifiers::Modifiers;
use crate::reserved::Os;

/// `Some(first code)` or `None` for an empty code list.
macro_rules! first {
    () => {
        None
    };
    ($first:literal $(, $rest:literal)*) => {
        Some($first)
    };
}

/// A row's keycap legend on `os`: the one legend, the Windows and the macOS one, or the key's
/// name when the row gives none.
macro_rules! keycap {
    ($os:ident, $name:ident) => {
        stringify!($name)
    };
    ($os:ident, $name:ident, $both:literal) => {
        $both
    };
    ($os:ident, $name:ident, $win:literal, $mac:literal) => {
        if $os == Os::MacOS {
            $mac
        } else {
            $win
        }
    };
}

macro_rules! keys {
    ($($name:ident = $value:literal, [$($alias:literal),*], win[$($vk:literal),*], mac[$($mac:literal),*], nx[$($nx:literal),*], cap[$($cap:literal),*];)*) => {
        /// A key a global hotkey can name. The variant names are exactly what .NET printed for
        /// Avalonia 12.1.2's `Key` and the values are Avalonia's, so settings saved by older builds
        /// (and the generated C# `HotkeyKey`) line up one to one. `None` means "no key / unmapped".
        #[repr(i32)]
        #[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
        pub enum Key {
            $($name = $value,)*
        }

        impl Key {
            /// Every key, in declaration order (= ascending value). Public for prisc_codegen.
            pub const ALL: &'static [Key] = &[$(Key::$name),*];

            /// The canonical name: what settings store and `Gesture` prints.
            pub fn name(self) -> &'static str {
                match self {
                    $(Key::$name => stringify!($name),)*
                }
            }

            /// Other names that parse to this key (Avalonia's duplicate enum names, e.g. `Snapshot`).
            /// Public for prisc_codegen.
            pub fn aliases(self) -> &'static [&'static str] {
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
            pub(crate) fn to_mac(self) -> Option<u16> {
                match self {
                    $(Key::$name => first!($($mac),*),)*
                }
            }

            /// The key for a macOS CGKeyCode (every listed code).
            pub(crate) fn from_mac(code: u16) -> Option<Key> {
                match code {
                    $($($mac => Some(Key::$name),)*)*
                    _ => None,
                }
            }

            /// The canonical macOS NX_KEYTYPE (system-defined media event) code.
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

            /// The legend on this key's keycap on `os`'s keyboards, as the keystroke overlay draws
            /// it ("Bksp" on Windows, "Delete" on macOS): the `cap` column, or the key's name when
            /// the row gives none. Linux has PC keyboards, so it reads the Windows legends. Not
            /// [`label`](Self::label), which names a key in a gesture ("Backspace" there).
            pub fn keycap(self, os: Os) -> &'static str {
                match self {
                    $(Key::$name => keycap!(os, $name $(, $cap)*),)*
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
// like Avalonia.Native, keypad Enter included as Return, and ISO keyboards' codes go through
// `mac_positional` first. JIS keys follow Avalonia.Native too: Eisu is HanjaMode, Kana
// HangulMode, Yen OemPipe, Ro OemBackslash, keypad comma AbntC2; keypad = shares OemPlus. NX is NSSystemDefined subtype 8 `data1 >> 16`.
//
// `cap` is the keycap legend the keystroke overlay draws for the key: one legend for both OSes, or
// the Windows one then the macOS one, or none for the key's name. Plain ASCII (bar the JIS and ISO
// legends macOS boards carry) on purpose: the overlay draws with the platform's default typeface,
// and a symbol glyph it happens not to carry renders as a missing-glyph box, so the symbol keys are
// spelled out (Bksp, Delete, Option) and drawn as vector icons where wanted. The macOS legends are
// Apple's, not the PC words for the same keys (Return, Delete, Option, Cmd, Fwd Del), so a Mac user
// watching their recording recognizes the caps they pressed. A code whose legend is not its row's
// (macOS keypad Enter, the JIS Yen and Ro keys) or that has no row (mouse buttons, Fn) is in
// `KEYCAP_EXTRA_WINDOWS` / `KEYCAP_EXTRA_MACOS`.
keys! {
    // Name                        =  value, [aliases], win[VK...], mac[CGKeyCode...], nx[NX_KEYTYPE...], cap[legend | win, mac];
    None                           =      0, [], win[], mac[], nx[], cap[];
    Cancel                         =      1, [], win[0x03], mac[], nx[], cap["Cancel"];
    Back                           =      2, [], win[0x08], mac[0x33], nx[], cap["Bksp", "Delete"];
    Tab                            =      3, [], win[0x09], mac[0x30], nx[], cap["Tab"];
    LineFeed                       =      4, [], win[], mac[], nx[], cap[];
    Clear                          =      5, [], win[0x0C], mac[0x47], nx[], cap["Clear"];
    Return                         =      6, ["Enter"], win[0x0D], mac[0x24, 0x4C], nx[], cap["Enter", "Return"];
    Pause                          =      7, [], win[0x13], mac[], nx[], cap["Pause"];
    CapsLock                       =      8, ["Capital"], win[0x14], mac[0x39], nx[], cap["Caps"];
    HangulMode                     =      9, ["KanaMode"], win[0x15], mac[0x68], nx[], cap["Kana"];
    JunjaMode                      =     10, [], win[0x17], mac[], nx[], cap["Junja"];
    FinalMode                      =     11, [], win[0x18], mac[], nx[], cap["Final"];
    HanjaMode                      =     12, ["KanjiMode"], win[0x19], mac[0x66], nx[], cap["Kanji", "Eisu"];
    Escape                         =     13, [], win[0x1B], mac[0x35], nx[], cap["Esc"];
    ImeConvert                     =     14, [], win[0x1C], mac[], nx[], cap["Convert"];
    ImeNonConvert                  =     15, [], win[0x1D], mac[], nx[], cap["NoConvert"];
    ImeAccept                      =     16, [], win[0x1E], mac[], nx[], cap["Accept"];
    ImeModeChange                  =     17, [], win[0x1F], mac[], nx[], cap["ModeChg"];
    Space                          =     18, [], win[0x20], mac[0x31], nx[], cap["Space"];
    PageUp                         =     19, ["Prior"], win[0x21], mac[0x74], nx[], cap["PgUp"];
    PageDown                       =     20, ["Next"], win[0x22], mac[0x79], nx[], cap["PgDn"];
    End                            =     21, [], win[0x23], mac[0x77], nx[], cap["End"];
    Home                           =     22, [], win[0x24], mac[0x73], nx[], cap["Home"];
    Left                           =     23, [], win[0x25], mac[0x7B], nx[], cap["Left"];
    Up                             =     24, [], win[0x26], mac[0x7E], nx[], cap["Up"];
    Right                          =     25, [], win[0x27], mac[0x7C], nx[], cap["Right"];
    Down                           =     26, [], win[0x28], mac[0x7D], nx[], cap["Down"];
    Select                         =     27, [], win[0x29], mac[], nx[], cap["Select"];
    Print                          =     28, [], win[0x2A], mac[], nx[], cap["Print"];
    Execute                        =     29, [], win[0x2B], mac[], nx[], cap["Exec"];
    PrintScreen                    =     30, ["Snapshot"], win[0x2C], mac[], nx[], cap["PrtSc"];
    Insert                         =     31, [], win[0x2D], mac[0x72], nx[], cap["Ins", "Help"];
    Delete                         =     32, [], win[0x2E], mac[0x75], nx[], cap["Del", "Fwd Del"];
    Help                           =     33, [], win[0x2F], mac[], nx[], cap["Help"];
    D0                             =     34, [], win[0x30], mac[0x1D], nx[], cap["0"];
    D1                             =     35, [], win[0x31], mac[0x12], nx[], cap["1"];
    D2                             =     36, [], win[0x32], mac[0x13], nx[], cap["2"];
    D3                             =     37, [], win[0x33], mac[0x14], nx[], cap["3"];
    D4                             =     38, [], win[0x34], mac[0x15], nx[], cap["4"];
    D5                             =     39, [], win[0x35], mac[0x17], nx[], cap["5"];
    D6                             =     40, [], win[0x36], mac[0x16], nx[], cap["6"];
    D7                             =     41, [], win[0x37], mac[0x1A], nx[], cap["7"];
    D8                             =     42, [], win[0x38], mac[0x1C], nx[], cap["8"];
    D9                             =     43, [], win[0x39], mac[0x19], nx[], cap["9"];
    A                              =     44, [], win[0x41], mac[0x00], nx[], cap["A"];
    B                              =     45, [], win[0x42], mac[0x0B], nx[], cap["B"];
    C                              =     46, [], win[0x43], mac[0x08], nx[], cap["C"];
    D                              =     47, [], win[0x44], mac[0x02], nx[], cap["D"];
    E                              =     48, [], win[0x45], mac[0x0E], nx[], cap["E"];
    F                              =     49, [], win[0x46], mac[0x03], nx[], cap["F"];
    G                              =     50, [], win[0x47], mac[0x05], nx[], cap["G"];
    H                              =     51, [], win[0x48], mac[0x04], nx[], cap["H"];
    I                              =     52, [], win[0x49], mac[0x22], nx[], cap["I"];
    J                              =     53, [], win[0x4A], mac[0x26], nx[], cap["J"];
    K                              =     54, [], win[0x4B], mac[0x28], nx[], cap["K"];
    L                              =     55, [], win[0x4C], mac[0x25], nx[], cap["L"];
    M                              =     56, [], win[0x4D], mac[0x2E], nx[], cap["M"];
    N                              =     57, [], win[0x4E], mac[0x2D], nx[], cap["N"];
    O                              =     58, [], win[0x4F], mac[0x1F], nx[], cap["O"];
    P                              =     59, [], win[0x50], mac[0x23], nx[], cap["P"];
    Q                              =     60, [], win[0x51], mac[0x0C], nx[], cap["Q"];
    R                              =     61, [], win[0x52], mac[0x0F], nx[], cap["R"];
    S                              =     62, [], win[0x53], mac[0x01], nx[], cap["S"];
    T                              =     63, [], win[0x54], mac[0x11], nx[], cap["T"];
    U                              =     64, [], win[0x55], mac[0x20], nx[], cap["U"];
    V                              =     65, [], win[0x56], mac[0x09], nx[], cap["V"];
    W                              =     66, [], win[0x57], mac[0x0D], nx[], cap["W"];
    X                              =     67, [], win[0x58], mac[0x07], nx[], cap["X"];
    Y                              =     68, [], win[0x59], mac[0x10], nx[], cap["Y"];
    Z                              =     69, [], win[0x5A], mac[0x06], nx[], cap["Z"];
    LWin                           =     70, [], win[0x5B], mac[0x37], nx[], cap["Win", "Cmd"];
    RWin                           =     71, [], win[0x5C], mac[0x36], nx[], cap["Win", "Cmd"];
    Apps                           =     72, [], win[0x5D], mac[0x6E], nx[], cap["Menu"];
    Sleep                          =     73, [], win[0x5F], mac[], nx[], cap["Sleep"];
    NumPad0                        =     74, [], win[0x60], mac[0x52], nx[], cap["0"];
    NumPad1                        =     75, [], win[0x61], mac[0x53], nx[], cap["1"];
    NumPad2                        =     76, [], win[0x62], mac[0x54], nx[], cap["2"];
    NumPad3                        =     77, [], win[0x63], mac[0x55], nx[], cap["3"];
    NumPad4                        =     78, [], win[0x64], mac[0x56], nx[], cap["4"];
    NumPad5                        =     79, [], win[0x65], mac[0x57], nx[], cap["5"];
    NumPad6                        =     80, [], win[0x66], mac[0x58], nx[], cap["6"];
    NumPad7                        =     81, [], win[0x67], mac[0x59], nx[], cap["7"];
    NumPad8                        =     82, [], win[0x68], mac[0x5B], nx[], cap["8"];
    NumPad9                        =     83, [], win[0x69], mac[0x5C], nx[], cap["9"];
    Multiply                       =     84, [], win[0x6A], mac[0x43], nx[], cap["*"];
    Add                            =     85, [], win[0x6B], mac[0x45], nx[], cap["+"];
    Separator                      =     86, [], win[0x6C], mac[], nx[], cap["Sep"];
    Subtract                       =     87, [], win[0x6D], mac[0x4E], nx[], cap["-"];
    Decimal                        =     88, [], win[0x6E], mac[0x41], nx[], cap["."];
    Divide                         =     89, [], win[0x6F], mac[0x4B], nx[], cap["/"];
    F1                             =     90, [], win[0x70], mac[0x7A], nx[], cap["F1"];
    F2                             =     91, [], win[0x71], mac[0x78], nx[], cap["F2"];
    F3                             =     92, [], win[0x72], mac[0x63], nx[], cap["F3"];
    F4                             =     93, [], win[0x73], mac[0x76], nx[], cap["F4"];
    F5                             =     94, [], win[0x74], mac[0x60], nx[], cap["F5"];
    F6                             =     95, [], win[0x75], mac[0x61], nx[], cap["F6"];
    F7                             =     96, [], win[0x76], mac[0x62], nx[], cap["F7"];
    F8                             =     97, [], win[0x77], mac[0x64], nx[], cap["F8"];
    F9                             =     98, [], win[0x78], mac[0x65], nx[], cap["F9"];
    F10                            =     99, [], win[0x79], mac[0x6D], nx[], cap["F10"];
    F11                            =    100, [], win[0x7A], mac[0x67], nx[], cap["F11"];
    F12                            =    101, [], win[0x7B], mac[0x6F], nx[], cap["F12"];
    F13                            =    102, [], win[0x7C], mac[0x69], nx[], cap["F13"];
    F14                            =    103, [], win[0x7D], mac[0x6B], nx[], cap["F14"];
    F15                            =    104, [], win[0x7E], mac[0x71], nx[], cap["F15"];
    F16                            =    105, [], win[0x7F], mac[0x6A], nx[], cap["F16"];
    F17                            =    106, [], win[0x80], mac[0x40], nx[], cap["F17"];
    F18                            =    107, [], win[0x81], mac[0x4F], nx[], cap["F18"];
    F19                            =    108, [], win[0x82], mac[0x50], nx[], cap["F19"];
    F20                            =    109, [], win[0x83], mac[0x5A], nx[], cap["F20"];
    F21                            =    110, [], win[0x84], mac[], nx[], cap["F21"];
    F22                            =    111, [], win[0x85], mac[], nx[], cap["F22"];
    F23                            =    112, [], win[0x86], mac[], nx[], cap["F23"];
    F24                            =    113, [], win[0x87], mac[], nx[], cap["F24"];
    NumLock                        =    114, [], win[0x90], mac[], nx[], cap["NumLk"];
    Scroll                         =    115, [], win[0x91], mac[], nx[], cap["ScrLk"];
    LeftShift                      =    116, [], win[0xA0, 0x10], mac[0x38], nx[], cap["Shift"];
    RightShift                     =    117, [], win[0xA1], mac[0x3C], nx[], cap["Shift"];
    LeftCtrl                       =    118, [], win[0xA2, 0x11], mac[0x3B], nx[], cap["Ctrl"];
    RightCtrl                      =    119, [], win[0xA3], mac[0x3E], nx[], cap["Ctrl"];
    LeftAlt                        =    120, [], win[0xA4, 0x12], mac[0x3A], nx[], cap["Alt", "Option"];
    RightAlt                       =    121, [], win[0xA5], mac[0x3D], nx[], cap["Alt", "Option"];
    BrowserBack                    =    122, [], win[0xA6], mac[], nx[], cap["Back"];
    BrowserForward                 =    123, [], win[0xA7], mac[], nx[], cap["Fwd"];
    BrowserRefresh                 =    124, [], win[0xA8], mac[], nx[], cap["Refresh"];
    BrowserStop                    =    125, [], win[0xA9], mac[], nx[], cap["Stop"];
    BrowserSearch                  =    126, [], win[0xAA], mac[], nx[], cap["Search"];
    BrowserFavorites               =    127, [], win[0xAB], mac[], nx[], cap["Favs"];
    BrowserHome                    =    128, [], win[0xAC], mac[], nx[], cap["Home"];
    VolumeMute                     =    129, [], win[0xAD], mac[0x4A], nx[7], cap["Mute"];
    VolumeDown                     =    130, [], win[0xAE], mac[0x49], nx[1], cap["Vol-"];
    VolumeUp                       =    131, [], win[0xAF], mac[0x48], nx[0], cap["Vol+"];
    MediaNextTrack                 =    132, [], win[0xB0], mac[], nx[17, 19], cap["Next"];
    MediaPreviousTrack             =    133, [], win[0xB1], mac[], nx[18, 20], cap["Prev"];
    MediaStop                      =    134, [], win[0xB2], mac[], nx[], cap["Stop"];
    MediaPlayPause                 =    135, [], win[0xB3], mac[], nx[16], cap["Play"];
    LaunchMail                     =    136, [], win[0xB4], mac[], nx[], cap["Mail"];
    SelectMedia                    =    137, [], win[0xB5], mac[], nx[], cap["Media"];
    LaunchApplication1             =    138, [], win[0xB6], mac[], nx[], cap["App1"];
    LaunchApplication2             =    139, [], win[0xB7], mac[], nx[], cap["App2"];
    OemSemicolon                   =    140, ["Oem1"], win[0xBA], mac[0x29], nx[], cap[";"];
    OemPlus                        =    141, [], win[0xBB], mac[0x18, 0x51], nx[], cap["="];
    OemComma                       =    142, [], win[0xBC], mac[0x2B], nx[], cap[","];
    OemMinus                       =    143, [], win[0xBD], mac[0x1B], nx[], cap["-"];
    OemPeriod                      =    144, [], win[0xBE], mac[0x2F], nx[], cap["."];
    OemQuestion                    =    145, ["Oem2"], win[0xBF], mac[0x2C], nx[], cap["/"];
    Oem3                           =    146, ["OemTilde"], win[0xC0], mac[0x32], nx[], cap["`"];
    AbntC1                         =    147, [], win[0xC1], mac[], nx[], cap[];
    AbntC2                         =    148, [], win[0xC2], mac[0x5F], nx[], cap["AbntC2", ","];
    Oem4                           =    149, ["OemOpenBrackets"], win[0xDB], mac[0x21], nx[], cap["["];
    OemPipe                        =    150, ["Oem5"], win[0xDC], mac[0x2A, 0x5D], nx[], cap["\\"];
    OemCloseBrackets               =    151, ["Oem6"], win[0xDD], mac[0x1E], nx[], cap["]"];
    OemQuotes                      =    152, ["Oem7"], win[0xDE], mac[0x27], nx[], cap["'"];
    Oem8                           =    153, [], win[0xDF], mac[], nx[], cap["OEM8"];
    OemBackslash                   =    154, ["Oem102"], win[0xE2], mac[0x0A, 0x5E], nx[], cap["\\", "§"];
    ImeProcessed                   =    155, [], win[0xE5], mac[], nx[], cap["Process"];
    System                         =    156, [], win[], mac[], nx[], cap[];
    OemAttn                        =    157, ["DbeAlphanumeric"], win[0xF0], mac[], nx[], cap[];
    OemFinish                      =    158, ["DbeKatakana"], win[0xF1], mac[], nx[], cap[];
    OemCopy                        =    159, ["DbeHiragana"], win[0xF2], mac[], nx[], cap[];
    OemAuto                        =    160, ["DbeSbcsChar"], win[0xF3], mac[], nx[], cap[];
    OemEnlw                        =    161, ["DbeDbcsChar"], win[0xF4], mac[], nx[], cap[];
    OemBackTab                     =    162, ["DbeRoman"], win[0xF5], mac[], nx[], cap[];
    DbeNoRoman                     =    163, ["Attn"], win[0xF6], mac[], nx[], cap["Attn"];
    CrSel                          =    164, ["DbeEnterWordRegisterMode"], win[0xF7], mac[], nx[], cap["CrSel"];
    ExSel                          =    165, ["DbeEnterImeConfigureMode"], win[0xF8], mac[], nx[], cap["ExSel"];
    DbeFlushString                 =    166, ["EraseEof"], win[0xF9], mac[], nx[], cap["ErEOF"];
    DbeCodeInput                   =    167, ["Play"], win[0xFA], mac[], nx[], cap["Play"];
    Zoom                           =    168, ["DbeNoCodeInput"], win[0xFB], mac[], nx[], cap["Zoom"];
    NoName                         =    169, ["DbeDetermineString"], win[0xFC], mac[], nx[], cap[];
    DbeEnterDialogConversionMode   =    170, ["Pa1"], win[0xFD], mac[], nx[], cap["PA1"];
    OemClear                       =    171, [], win[0xFE], mac[], nx[], cap["Clear"];
    DeadCharProcessed              =    172, [], win[], mac[], nx[], cap[];
    FnLeftArrow                    =  10001, [], win[], mac[], nx[], cap[];
    FnRightArrow                   =  10002, [], win[], mac[], nx[], cap[];
    FnUpArrow                      =  10003, [], win[], mac[], nx[], cap[];
    FnDownArrow                    =  10004, [], win[], mac[], nx[], cap[];
    MediaHome                      = 100000, [], win[], mac[], nx[], cap[];
    MediaChannelList               = 100001, [], win[], mac[], nx[], cap[];
    MediaChannelRaise              = 100002, [], win[], mac[], nx[], cap[];
    MediaChannelLower              = 100003, [], win[], mac[], nx[], cap[];
    MediaRecord                    = 100005, [], win[], mac[], nx[], cap[];
    MediaRed                       = 100010, [], win[], mac[], nx[], cap[];
    MediaGreen                     = 100011, [], win[], mac[], nx[], cap[];
    MediaYellow                    = 100012, [], win[], mac[], nx[], cap[];
    MediaBlue                      = 100013, [], win[], mac[], nx[], cap[];
    MediaMenu                      = 100020, [], win[], mac[], nx[], cap[];
    MediaMore                      = 100021, [], win[], mac[], nx[], cap[];
    MediaOption                    = 100022, [], win[], mac[], nx[], cap[];
    MediaInfo                      = 100023, [], win[], mac[], nx[], cap[];
    MediaSearch                    = 100024, [], win[], mac[], nx[], cap[];
    MediaSubtitle                  = 100025, [], win[], mac[], nx[], cap[];
    MediaTvGuide                   = 100026, [], win[], mac[], nx[], cap[];
    MediaPreviousChannel           = 100027, [], win[], mac[], nx[], cap[];
}

/// The position-true CGKeyCode for a code from a keyboard that is `iso`. ISO keyboards report the
/// key left of 1 as kVK_ISO_Section (0x0A) and the key beside left Shift as kVK_ANSI_Grave (0x32),
/// the other way round from where those names sit; swapping them back (as Chromium and WebKit do)
/// keeps a gesture on the same physical key across ANSI and ISO keyboards.
#[cfg_attr(not(target_os = "macos"), allow(dead_code))]
pub(crate) fn mac_positional(code: u16, iso: bool) -> u16 {
    match (iso, code) {
        (true, 0x0A) => 0x32,
        (true, 0x32) => 0x0A,
        _ => code,
    }
}

/// Windows virtual-key codes whose keycap legend is not their row's `cap`, or that have no row:
/// the mouse buttons (the overlay shows clicks held in a chord) and VK_PACKET.
pub const KEYCAP_EXTRA_WINDOWS: &[(u16, &str)] = &[
    (0x01, "LMouse"),
    (0x02, "RMouse"),
    (0x04, "MMouse"),
    (0x05, "Mouse4"),
    (0x06, "Mouse5"),
    (0xE7, "Packet"),
];

/// macOS CGKeyCodes whose keycap legend is not their row's `cap`, or that have no row: Fn, the
/// keypad's own Enter (a separate key from Return, which shares its hotkey row), and the JIS Yen
/// and Ro (underscore) keys, which share `OemPipe` and `OemBackslash` with the ANSI and ISO keys.
pub const KEYCAP_EXTRA_MACOS: &[(u16, &str)] = &[(Key::MAC_FN_KEYCODE, "Fn"), (0x4C, "Enter"), (0x5D, "¥"), (0x5E, "_")];

impl Key {
    /// macOS's Fn (Globe) key, kVK_Function. It has no row (Windows never reports it, and a hotkey
    /// cannot name it), but the macOS hook reports it as a modifier edge of its own, so the
    /// keystroke overlay treats it as a modifier role beside [`Modifiers`]' four.
    pub const MAC_FN_KEYCODE: u16 = 0x3F;

    /// The keycap legend for a raw key code off `os`'s keyboard (a Windows virtual-key code or a
    /// macOS CGKeyCode, the numbering the hook reports as `raw_code` and a recording stores), or
    /// `None` for a code with no key. Linux has no code numbering of its own here, so `None`.
    pub fn keycap_for_code(code: u16, os: Os) -> Option<&'static str> {
        let (extra, key) = match os {
            Os::Windows => (KEYCAP_EXTRA_WINDOWS, Key::from_vk(code)),
            Os::MacOS => (KEYCAP_EXTRA_MACOS, Key::from_mac(code)),
            Os::Linux => return None,
        };
        extra
            .iter()
            .find(|&&(extra_code, _)| extra_code == code)
            .map(|&(_, legend)| legend)
            .or_else(|| key.map(|key| key.keycap(os)))
    }

    /// The modifier a raw key code off `os`'s keyboard adds when held (either side's key), or
    /// `Modifiers::NONE` for a code that is not a modifier key. Fn is not one of `Modifiers`, so
    /// `MAC_FN_KEYCODE` answers `NONE`; Linux has no code numbering here, so `NONE`.
    pub fn modifier_for_code(code: u16, os: Os) -> Modifiers {
        let key = match os {
            Os::Windows => Key::from_vk(code),
            Os::MacOS => Key::from_mac(code),
            Os::Linux => None,
        };
        key.map_or(Modifiers::NONE, Key::modifier)
    }

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
        self.modifier() != Modifiers::NONE
    }

    /// The side-agnostic modifier this key adds when held (either side's key: Shift for LeftShift
    /// and RightShift, Meta for LWin and RWin), or `Modifiers::NONE` for a key that is not one.
    pub fn modifier(self) -> Modifiers {
        match self {
            Key::LWin | Key::RWin => Modifiers::META,
            Key::LeftShift | Key::RightShift => Modifiers::SHIFT,
            Key::LeftCtrl | Key::RightCtrl => Modifiers::CONTROL,
            Key::LeftAlt | Key::RightAlt => Modifiers::ALT,
            _ => Modifiers::NONE,
        }
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
            115
        );
    }

    #[test]
    fn names_aliases_and_labels_are_unique_ignoring_case() {
        // from_name takes the first match ignoring ASCII case, so a clash would make a name ambiguous.
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

    /// The JIS-only keys, and keypad `=`, map as Avalonia.Native 12.1.2 maps them (its table in
    /// libAvaloniaNative.dylib), which is also what Windows calls the Yen and Ro keys.
    #[test]
    fn jis_keys_map_like_avalonia_native() {
        assert_eq!(Key::from_mac(0x66), Some(Key::HanjaMode)); // kVK_JIS_Eisu
        assert_eq!(Key::from_mac(0x68), Some(Key::HangulMode)); // kVK_JIS_Kana
        assert_eq!(Key::from_mac(0x5D), Some(Key::OemPipe)); // kVK_JIS_Yen
        assert_eq!(Key::from_mac(0x5E), Some(Key::OemBackslash)); // kVK_JIS_Underscore (Ro)
        assert_eq!(Key::from_mac(0x5F), Some(Key::AbntC2)); // kVK_JIS_KeypadComma
        assert_eq!(Key::from_mac(0x51), Some(Key::OemPlus)); // kVK_ANSI_KeypadEquals

        // Keys that only JIS has register by their JIS code; shared names keep the ANSI one.
        assert_eq!(Key::HanjaMode.to_mac(), Some(0x66));
        assert_eq!(Key::AbntC2.to_mac(), Some(0x5F));
        assert_eq!(Key::OemPipe.to_mac(), Some(0x2A));
        assert_eq!(Key::OemBackslash.to_mac(), Some(0x0A));
        assert_eq!(Key::OemPlus.to_mac(), Some(0x18));
    }

    #[test]
    fn iso_keyboards_swap_section_and_grave_back() {
        // ISO: the key left of 1 reports 0x0A and the key beside left Shift reports 0x32.
        assert_eq!(Key::from_mac(mac_positional(0x0A, true)), Some(Key::Oem3));
        assert_eq!(Key::from_mac(mac_positional(0x32, true)), Some(Key::OemBackslash));
        assert_eq!(mac_positional(0x0A, false), 0x0A);
        assert_eq!(mac_positional(0x32, false), 0x32);
        for code in (0..0x80).filter(|code| ![0x0A, 0x32].contains(code)) {
            assert_eq!(mac_positional(code, true), code);
        }
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
        assert_eq!(Key::RWin.modifier(), Modifiers::META);
        assert_eq!(Key::RightShift.modifier(), Modifiers::SHIFT);
        assert_eq!(Key::LeftCtrl.modifier(), Modifiers::CONTROL);
        assert_eq!(Key::RightAlt.modifier(), Modifiers::ALT);
        assert_eq!(Key::CapsLock.modifier(), Modifiers::NONE);
        assert_eq!(Key::modifier_for_code(0x10, Os::Windows), Modifiers::SHIFT);
        assert_eq!(Key::modifier_for_code(0x5C, Os::Windows), Modifiers::META);
        assert_eq!(Key::modifier_for_code(0x3D, Os::MacOS), Modifiers::ALT);
        assert_eq!(Key::modifier_for_code(Key::MAC_FN_KEYCODE, Os::MacOS), Modifiers::NONE);
        assert_eq!(Key::modifier_for_code(0x10, Os::Linux), Modifiers::NONE);
    }

    /// The legends the keystroke overlay drew from its hand tables before they moved here
    /// (KeyboardLayoutTests), per OS numbering.
    #[test]
    fn keycaps_per_code_are_the_overlay_legends() {
        for (vk, legend) in [
            (37, "Left"),
            (112, "F1"),
            (135, "F24"),
            (65, "A"),
            (55, "7"),
            (96, "0"),
            (220, "\\"),
            (226, "\\"),
            (32, "Space"),
            (8, "Bksp"),
            (13, "Enter"),
            (0x10, "Shift"),
            (0xA1, "Shift"),
            (0x12, "Alt"),
            (91, "Win"),
            (44, "PrtSc"),
            (46, "Del"),
            (1, "LMouse"),
            (6, "Mouse5"),
            (231, "Packet"),
            (173, "Mute"),
            (250, "Play"),
            (179, "Play"),
            (254, "Clear"),
        ] {
            assert_eq!(Key::keycap_for_code(vk, Os::Windows), Some(legend), "VK {vk}");
        }
        for (code, legend) in [
            (0, "A"),
            (13, "W"),
            (10, "§"),
            (94, "_"),
            (42, "\\"),
            (93, "¥"),
            (18, "1"),
            (22, "6"),
            (23, "5"),
            (36, "Return"),
            (76, "Enter"),
            (48, "Tab"),
            (49, "Space"),
            (51, "Delete"),
            (117, "Fwd Del"),
            (114, "Help"),
            (53, "Esc"),
            (55, "Cmd"),
            (58, "Option"),
            (59, "Ctrl"),
            (57, "Caps"),
            (63, "Fn"),
            (82, "0"),
            (92, "9"),
            (95, ","),
            (102, "Eisu"),
            (104, "Kana"),
            (122, "F1"),
            (111, "F12"),
            (90, "F20"),
            (123, "Left"),
            (126, "Up"),
        ] {
            assert_eq!(Key::keycap_for_code(code, Os::MacOS), Some(legend), "CGKeyCode {code}");
        }
        // unassigned codes have no legend; the overlay draws the raw code
        assert_eq!(Key::keycap_for_code(232, Os::Windows), None);
        assert_eq!(Key::keycap_for_code(0, Os::Windows), None);
        assert_eq!(Key::keycap_for_code(52, Os::MacOS), None);
        assert_eq!(Key::keycap_for_code(0x24, Os::Linux), None);
    }

    /// Every key with a code on an OS has a legend there. The rows that leave `cap` empty while
    /// having a code show their key name (no legend is known for these rare OEM and ABNT keys).
    #[test]
    fn keycap_column_covers_every_key_with_a_code() {
        for &key in Key::ALL {
            for (os, code) in [(Os::Windows, key.to_vk()), (Os::MacOS, key.to_mac())] {
                let Some(code) = code else { continue };
                let legend = Key::keycap_for_code(code, os).unwrap_or_else(|| panic!("{key:?} on {os:?}"));
                assert!(!legend.trim().is_empty(), "{key:?} on {os:?}");
            }
        }
        for &(code, legend) in KEYCAP_EXTRA_WINDOWS {
            assert_eq!(Key::keycap_for_code(code, Os::Windows), Some(legend));
        }
        for &(code, legend) in KEYCAP_EXTRA_MACOS {
            assert_eq!(Key::keycap_for_code(code, Os::MacOS), Some(legend));
        }
        assert_eq!(Key::keycap_for_code(0xC1, Os::Windows), Some("AbntC1"));
        assert_eq!(Key::keycap_for_code(0xFC, Os::Windows), Some("NoName"));
        assert_eq!(Key::Back.keycap(Os::Linux), "Bksp");
        assert_eq!(Key::Back.keycap(Os::MacOS), "Delete");
    }
}
