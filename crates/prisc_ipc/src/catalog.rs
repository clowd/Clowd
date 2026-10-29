//! The message catalogs (HELPER_PROTOCOL.md §4), one module per catalog.
//!
//! Each `ipc_catalog!` invocation below is the only place its messages are written. It expands
//! into the serde types a helper emits and parses (`Event`, `Command`, the string enums and
//! records) and into `SPEC`, the same catalog as data, from which [`CatalogSpec::catalog_hash`]
//! and the C# mirror (`src/csharp.rs`) are produced. The common `hello`, `error` and `quit`
//! (§2.2, §2.3) are injected into every catalog.
//!
//! DSL notes: message and string-enum value names are written in PascalCase and go on the wire
//! in snake_case (`StartedRecording` is `started_recording`, `Screen | Monitor` accepts
//! `monitor` on decode); field names are written as they go on the wire. A field type is one of
//! `bool u32 u64 i32 i64 f64 String`, a string enum or record of the same catalog, or
//! `Vec<T>` / `Option<T>` of one of those. Every message carries a `sample`, written exactly as
//! serde_json writes it (whole-number floats as `30.0`): the tests round-trip each one byte for
//! byte here and by value in Prisc.Ipc.Tests.

use std::fmt;
use std::time::Duration;

use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize, Serializer};

// --- the catalog as data ---------------------------------------------------------------------

/// A field's type.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TypeSpec {
    Bool,
    U32,
    U64,
    I32,
    I64,
    F64,
    String,
    Vec(&'static TypeSpec),
    Option(&'static TypeSpec),
    /// A string enum or record of the same catalog, or the common `ErrorCode`.
    Named(&'static str),
}

impl fmt::Display for TypeSpec {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Bool => f.write_str("bool"),
            Self::U32 => f.write_str("u32"),
            Self::U64 => f.write_str("u64"),
            Self::I32 => f.write_str("i32"),
            Self::I64 => f.write_str("i64"),
            Self::F64 => f.write_str("f64"),
            Self::String => f.write_str("String"),
            Self::Vec(inner) => write!(f, "Vec<{inner}>"),
            Self::Option(inner) => write!(f, "Option<{inner}>"),
            Self::Named(name) => f.write_str(name),
        }
    }
}

#[derive(Debug)]
pub struct FieldSpec {
    /// The wire name (snake_case).
    pub name: &'static str,
    pub ty: TypeSpec,
}

#[derive(Debug)]
pub struct MessageSpec {
    /// The wire `type` (snake_case).
    pub name: &'static str,
    /// The Rust variant name (PascalCase), also the C# record name.
    pub ident: &'static str,
    /// Ends the run (§1.4). Commands are never terminal.
    pub terminal: bool,
    pub fields: &'static [FieldSpec],
    /// One wire line, as serde_json writes it.
    pub sample: &'static str,
}

#[derive(Debug)]
pub struct ValueSpec {
    /// The wire string (snake_case).
    pub name: &'static str,
    /// The Rust variant name (PascalCase), also the C# enum member.
    pub ident: &'static str,
    /// Wire strings accepted on decode only.
    pub aliases: &'static [&'static str],
}

/// A string enum (`strings Name { .. }`).
#[derive(Debug)]
pub struct StringsSpec {
    pub name: &'static str,
    pub values: &'static [ValueSpec],
}

/// A nested object type (`record Name { .. }`).
#[derive(Debug)]
pub struct RecordSpec {
    pub name: &'static str,
    pub fields: &'static [FieldSpec],
}

/// One catalog: what a helper (or helpers) emits and accepts. The common `hello`, `error`,
/// `quit` and `ErrorCode` are not listed here; they are [`HELLO`], [`ERROR`], [`QUIT`] and
/// [`ErrorCode::SPEC`], shared by every catalog.
#[derive(Debug)]
pub struct CatalogSpec {
    pub name: &'static str,
    /// Binary stems that speak it, as sent in `hello.helper`.
    pub helpers: &'static [&'static str],
    /// The parent's wait between `quit` and kill (§7.3).
    pub quit_grace_ms: u64,
    pub strings: &'static [StringsSpec],
    pub records: &'static [RecordSpec],
    pub events: &'static [MessageSpec],
    pub commands: &'static [MessageSpec],
}

impl CatalogSpec {
    pub const fn quit_grace(&self) -> Duration {
        Duration::from_millis(self.quit_grace_ms)
    }

    /// The canonical text the hash is taken over: the common messages, then every string enum,
    /// record, event and command of the catalog in declaration order, with field names and types,
    /// enum values and aliases, and terminal flags. Helpers, quit grace and Rust identifiers are
    /// left out: they do not change what goes on the wire.
    pub fn canonical(&self) -> String {
        fn fields(out: &mut String, fields: &[FieldSpec]) {
            for field in fields {
                out.push_str(&format!(" {}:{}", field.name, field.ty));
            }
            out.push('\n');
        }
        fn strings(out: &mut String, spec: &StringsSpec) {
            out.push_str(&format!("strings {}", spec.name));
            for value in spec.values {
                out.push(' ');
                out.push_str(value.name);
                for alias in value.aliases {
                    out.push('|');
                    out.push_str(alias);
                }
            }
            out.push('\n');
        }
        fn message(out: &mut String, kind: &str, spec: &MessageSpec) {
            let terminal = if spec.terminal { " terminal" } else { "" };
            out.push_str(&format!("{kind} {}{terminal}", spec.name));
            fields(out, spec.fields);
        }

        let mut out = format!("catalog {}\n", self.name);
        strings(&mut out, &ErrorCode::SPEC);
        for spec in self.strings {
            strings(&mut out, spec);
        }
        for spec in self.records {
            out.push_str(&format!("record {}", spec.name));
            fields(&mut out, spec.fields);
        }
        for spec in [&HELLO, &ERROR]
            .into_iter()
            .chain(self.events)
        {
            message(&mut out, "event", spec);
        }
        for spec in [&QUIT].into_iter().chain(self.commands) {
            message(&mut out, "command", spec);
        }
        out
    }

    /// The `hello.catalog_hash` fingerprint (§2.5): 64-bit FNV-1a over [`Self::canonical`],
    /// 16 lower-case hex digits.
    pub fn catalog_hash(&self) -> String {
        format!("{:016x}", fnv1a64(self.canonical().as_bytes()))
    }
}

fn fnv1a64(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for &byte in bytes {
        hash ^= u64::from(byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}

/// Every catalog, for the generator and the tests.
pub const ALL: &[&CatalogSpec] = &[&recorder::SPEC, &share_region::SPEC, &job::SPEC, &scroll::SPEC, &capture::SPEC];

// --- traits the channel works over -----------------------------------------------------------

/// A catalog's `Event` enum.
pub trait Event: Serialize + DeserializeOwned + Send + 'static {
    const SPEC: &'static CatalogSpec;
    /// The catalog's `error` event.
    fn error(code: ErrorCode, message: String, fatal: bool) -> Self;
    /// A terminal event, or a fatal `error` (§1.4).
    fn is_terminal(&self) -> bool;
}

/// A catalog's `Command` enum. Never contains `quit` (§2.3).
pub trait Command: Serialize + DeserializeOwned + Send + 'static {
    const SPEC: &'static CatalogSpec;
}

// --- serialization plumbing --------------------------------------------------------------------

/// How a catalog field goes on the wire: whether it is omitted, and floats forced finite (§2.1).
/// Every generated field is serialized through it.
#[doc(hidden)]
pub trait Wire {
    fn omit(&self) -> bool {
        false
    }
    fn wire<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error>;
}

macro_rules! wire_as_is {
    ($($ty:ty),*) => {$(
        impl Wire for $ty {
            fn wire<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
                Serialize::serialize(self, serializer)
            }
        }
    )*};
}
wire_as_is!(bool, u32, u64, i32, i64, String);

impl Wire for f64 {
    fn wire<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        debug_assert!(self.is_finite(), "non-finite float in a protocol message: {self}");
        serializer.serialize_f64(if self.is_finite() { *self } else { 0.0 })
    }
}

impl<T: Wire> Wire for Vec<T> {
    fn wire<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_seq(self.iter().map(AsWire))
    }
}

impl<T: Wire> Wire for Option<T> {
    fn omit(&self) -> bool {
        self.is_none()
    }
    fn wire<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        match self {
            Some(value) => serializer.serialize_some(&AsWire(value)),
            None => serializer.serialize_none(),
        }
    }
}

struct AsWire<'a, T>(&'a T);

impl<T: Wire> Serialize for AsWire<'_, T> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        self.0.wire(serializer)
    }
}

#[doc(hidden)]
pub const fn snake_len(ident: &str) -> usize {
    let bytes = ident.as_bytes();
    let mut len = 0;
    let mut i = 0;
    while i < bytes.len() {
        if i > 0 && bytes[i].is_ascii_uppercase() {
            len += 1;
        }
        len += 1;
        i += 1;
    }
    len
}

/// `StartedRecording` → `started_recording`, the same rule as serde's `rename_all = "snake_case"`.
#[doc(hidden)]
pub const fn snake_bytes<const N: usize>(ident: &str) -> [u8; N] {
    let bytes = ident.as_bytes();
    let mut out = [0u8; N];
    let mut i = 0;
    let mut j = 0;
    while i < bytes.len() {
        if i > 0 && bytes[i].is_ascii_uppercase() {
            out[j] = b'_';
            j += 1;
        }
        out[j] = bytes[i].to_ascii_lowercase();
        j += 1;
        i += 1;
    }
    out
}

// --- the macros --------------------------------------------------------------------------------

/// A PascalCase identifier's snake_case wire name, as a `&'static str` constant.
macro_rules! snake {
    ($ident:ident) => {{
        const N: usize = $crate::catalog::snake_len(stringify!($ident));
        const BYTES: [u8; N] = $crate::catalog::snake_bytes::<N>(stringify!($ident));
        const NAME: &str = match ::std::str::from_utf8(&BYTES) {
            Ok(name) => name,
            Err(_) => panic!("identifier is not ASCII"),
        };
        NAME
    }};
}

/// A field type's [`TypeSpec`], from its tokens (`Option String`, `u32`, `Rect`).
macro_rules! type_spec {
    (Option $inner:ident) => {
        $crate::catalog::TypeSpec::Option(&type_spec!($inner))
    };
    (Vec $inner:ident) => {
        $crate::catalog::TypeSpec::Vec(&type_spec!($inner))
    };
    (bool) => {
        $crate::catalog::TypeSpec::Bool
    };
    (u32) => {
        $crate::catalog::TypeSpec::U32
    };
    (u64) => {
        $crate::catalog::TypeSpec::U64
    };
    (i32) => {
        $crate::catalog::TypeSpec::I32
    };
    (i64) => {
        $crate::catalog::TypeSpec::I64
    };
    (f64) => {
        $crate::catalog::TypeSpec::F64
    };
    (String) => {
        $crate::catalog::TypeSpec::String
    };
    ($named:ident) => {
        $crate::catalog::TypeSpec::Named(stringify!($named))
    };
}

/// `event` or `terminal`, the kind keyword in front of an event.
macro_rules! is_terminal {
    (event) => {
        false
    };
    (terminal) => {
        true
    };
}

/// A string enum: the Rust enum, its serde impls (snake_case strings, aliases on decode) and
/// its `SPEC`.
macro_rules! ipc_strings {
    ($(#[$meta:meta])* $name:ident { $($value:ident $(| $alias:ident)*),* $(,)? }) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
        pub enum $name {
            $($value),*
        }

        impl $name {
            pub const SPEC: $crate::catalog::StringsSpec = $crate::catalog::StringsSpec {
                name: stringify!($name),
                values: &[$($crate::catalog::ValueSpec {
                    name: snake!($value),
                    ident: stringify!($value),
                    aliases: &[$(snake!($alias)),*],
                }),*],
            };

            /// The wire string.
            pub const fn as_str(self) -> &'static str {
                match self {
                    $(Self::$value => snake!($value)),*
                }
            }

            /// Decodes a wire string or alias.
            pub fn from_wire(text: &str) -> Option<Self> {
                $(
                    if text == snake!($value) $(|| text == snake!($alias))* {
                        return Some(Self::$value);
                    }
                )*
                None
            }
        }

        impl ::serde::Serialize for $name {
            fn serialize<S: ::serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
                serializer.serialize_str(self.as_str())
            }
        }

        impl<'de> ::serde::Deserialize<'de> for $name {
            fn deserialize<D: ::serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
                let text = <::std::borrow::Cow<'de, str>>::deserialize(deserializer)?;
                Self::from_wire(&text).ok_or_else(|| {
                    <D::Error as ::serde::de::Error>::custom(format!(
                        "unknown {} value '{}'",
                        stringify!($name),
                        text
                    ))
                })
            }
        }

        impl $crate::catalog::Wire for $name {
            fn wire<S: ::serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
                ::serde::Serialize::serialize(self, serializer)
            }
        }
    };
}

/// A record: the Rust struct, its serde derives and its `SPEC`.
macro_rules! ipc_record {
    ($(#[$meta:meta])* $name:ident { $($field:ident : $ty:ident $(<$inner:ident>)?),* $(,)? }) => {
        #[derive(Debug, Clone, PartialEq, ::serde::Serialize, ::serde::Deserialize)]
        $(#[$meta])*
        pub struct $name {
            $(
                #[serde(skip_serializing_if = "crate::catalog::Wire::omit", serialize_with = "crate::catalog::Wire::wire")]
                pub $field: $ty $(<$inner>)?,
            )*
        }

        impl $name {
            pub const SPEC: $crate::catalog::RecordSpec = $crate::catalog::RecordSpec {
                name: stringify!($name),
                fields: &[$($crate::catalog::FieldSpec { name: stringify!($field), ty: type_spec!($ty $($inner)?) }),*],
            };
        }

        impl $crate::catalog::Wire for $name {
            fn wire<S: ::serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
                ::serde::Serialize::serialize(self, serializer)
            }
        }
    };
}

/// A message's [`MessageSpec`].
macro_rules! message_spec {
    ($terminal:expr, $name:ident { $($field:ident : $ty:ident $(<$inner:ident>)?),* }, $sample:literal) => {
        $crate::catalog::MessageSpec {
            name: snake!($name),
            ident: stringify!($name),
            terminal: $terminal,
            fields: &[$($crate::catalog::FieldSpec { name: stringify!($field), ty: type_spec!($ty $($inner)?) }),*],
            sample: $sample,
        }
    };
}

/// One catalog (HELPER_PROTOCOL.md §6): `pub mod <catalog>` with `Event`, `Command`, the string
/// enums and records, `SPEC`, `QUIT_GRACE` and `HELPERS`.
macro_rules! ipc_catalog {
    (
        catalog $catalog:ident;
        helpers [$($helper:literal),* $(,)?];
        quit_grace_ms $grace:literal;
        $(strings $sname:ident { $($svalue:ident $(| $salias:ident)*),* $(,)? })*
        $(record $rname:ident { $($rfield:ident : $rty:ident $(<$rinner:ident>)?),* $(,)? })*
        events {
            $($ekind:ident $ename:ident { $($efield:ident : $ety:ident $(<$einner:ident>)?),* $(,)? }
                sample $esample:literal;)*
        }
        commands {
            $($cname:ident { $($cfield:ident : $cty:ident $(<$cinner:ident>)?),* $(,)? }
                sample $csample:literal;)*
        }
    ) => {
        pub mod $catalog {
            use super::ErrorCode;

            /// Binary stems that speak this catalog, as sent in `hello.helper`.
            pub const HELPERS: &[&str] = &[$($helper),*];
            /// The parent's wait between `quit` and kill (§7.3).
            pub const QUIT_GRACE: ::std::time::Duration = ::std::time::Duration::from_millis($grace);

            $(ipc_strings!($sname { $($svalue $(| $salias)*),* });)*
            $(ipc_record!($rname { $($rfield : $rty $(<$rinner>)?),* });)*

            /// What the helper writes to stdout. `hello` is not here: `prisc_ipc` writes it.
            #[derive(Debug, Clone, PartialEq, ::serde::Serialize, ::serde::Deserialize)]
            #[serde(tag = "type", rename_all = "snake_case")]
            pub enum Event {
                $($ename {
                    $(
                        #[serde(skip_serializing_if = "crate::catalog::Wire::omit", serialize_with = "crate::catalog::Wire::wire")]
                        $efield: $ety $(<$einner>)?,
                    )*
                },)*
                Error { code: ErrorCode, message: String, fatal: bool },
            }

            /// What the helper reads from stdin. `quit` is not here: `prisc_ipc` handles it.
            #[derive(Debug, Clone, PartialEq, ::serde::Serialize, ::serde::Deserialize)]
            #[serde(tag = "type", rename_all = "snake_case")]
            pub enum Command {
                $($cname {
                    $(
                        #[serde(skip_serializing_if = "crate::catalog::Wire::omit", serialize_with = "crate::catalog::Wire::wire")]
                        $cfield: $cty $(<$cinner>)?,
                    )*
                },)*
            }

            pub const SPEC: $crate::catalog::CatalogSpec = $crate::catalog::CatalogSpec {
                name: stringify!($catalog),
                helpers: HELPERS,
                quit_grace_ms: $grace,
                strings: &[$($sname::SPEC),*],
                records: &[$($rname::SPEC),*],
                events: &[$(message_spec!(is_terminal!($ekind), $ename { $($efield : $ety $(<$einner>)?),* }, $esample)),*],
                commands: &[$(message_spec!(false, $cname { $($cfield : $cty $(<$cinner>)?),* }, $csample)),*],
            };

            impl $crate::catalog::Event for Event {
                const SPEC: &'static $crate::catalog::CatalogSpec = &SPEC;

                fn error(code: ErrorCode, message: String, fatal: bool) -> Self {
                    Self::Error { code, message, fatal }
                }

                fn is_terminal(&self) -> bool {
                    match self {
                        $(Self::$ename { .. } => is_terminal!($ekind),)*
                        Self::Error { fatal, .. } => *fatal,
                    }
                }
            }

            impl $crate::catalog::Command for Command {
                const SPEC: &'static $crate::catalog::CatalogSpec = &SPEC;
            }

            /// Round-trips every sample through the Rust types.
            #[cfg(test)]
            pub(crate) fn check_samples() {
                use super::tests::check_sample;
                $(check_sample::<Event>(snake!($ename), $esample);)*
                check_sample::<Event>(super::ERROR.name, super::ERROR.sample);
                $(check_sample::<Command>(snake!($cname), $csample);)*
            }
        }
    };
}

// --- common to every catalog (§2.2, §2.3) -----------------------------------------------------------

ipc_strings! {
    /// `error.code` (§2.2). A closed set; the parent treats an unknown code as fatal.
    ErrorCode { BadCommand, Rejected, Usage, InitFailed, IoFailed, Permission, Failed }
}

ipc_record! {
    /// The first line every line-protocol helper writes (§2.2).
    #[serde(tag = "type", rename = "hello")]
    Hello { protocol: u32, catalog: String, catalog_hash: String, helper: String, version: String, pid: u32 }
}

/// `{"type":"quit"}` (§2.3), handled by `prisc_ipc` itself and never part of a `Command` enum.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(tag = "type", rename = "quit")]
pub struct Quit {}

pub const HELLO: MessageSpec = MessageSpec {
    name: "hello",
    ident: "Hello",
    terminal: false,
    fields: Hello::SPEC.fields,
    sample: r#"{"type":"hello","protocol":1,"catalog":"recorder","catalog_hash":"9f3c0a7e12b4d5e6","helper":"prisc_recorder","version":"4.11.0","pid":4242}"#,
};

pub const ERROR: MessageSpec = message_spec!(
    false,
    Error {
        code: ErrorCode,
        message: String,
        fatal: bool
    },
    r#"{"type":"error","code":"bad_command","message":"unknown command 'mvoe'","fatal":false}"#
);

pub const QUIT: MessageSpec = message_spec!(false, Quit {}, r#"{"type":"quit"}"#);

// --- the catalogs (§4) -------------------------------------------------------------------------------

// §4.1. QUIT_GRACE covers the recorder's own stop path: STOP_DEADLINE (30 s) for the output's
// stop signal, FLUSH_DEADLINE (30 s) for the mp4 flush, plus 5 s; prisc_recorder asserts it.
ipc_catalog! {
    catalog recorder;
    helpers ["prisc_recorder"];
    quit_grace_ms 65000;
    strings RecorderResult { Stopped, Cancelled, Failed }
    strings AudioKind { Speaker, Microphone, Mixed }
    record Tracks { screen: VideoTrack, webcam: Option<VideoTrack>, audio: Vec<AudioTrack> }
    record VideoTrack { index: u32, width: u32, height: u32 }
    record AudioTrack { index: u32, kind: AudioKind, device: Option<String>, name: String }
    record Camera { id: String, name: String }
    events {
        event Initialized {}
            sample r#"{"type":"initialized"}"#;
        event StartedRecording { tracks: Tracks, input_capture: Option<String>, window_capture: Option<String> }
            sample r#"{"type":"started_recording","tracks":{"screen":{"index":0,"width":2560,"height":1440},"webcam":{"index":1,"width":1280,"height":720},"audio":[{"index":1,"kind":"speaker","device":"default","name":"Speakers"},{"index":2,"kind":"microphone","name":"Microphone"}]},"input_capture":"/tmp/rec/input.jsonl","window_capture":"/tmp/rec/windows.jsonl"}"#;
        event RecordingPaused {}
            sample r#"{"type":"recording_paused"}"#;
        event RecordingResumed {}
            sample r#"{"type":"recording_resumed"}"#;
        event Status { time_ms: u64, fps: f64, dropped: i64, dropped_perc: f64 }
            sample r#"{"type":"status","time_ms":61250,"fps":30.0,"dropped":2,"dropped_perc":0.1}"#;
        event Levels { speaker: Vec<f64>, mic: Vec<f64> }
            sample r#"{"type":"levels","speaker":[-12.5,-100.0],"mic":[-30.25]}"#;
        event ConfigureApplied { ignored_keys: Vec<String> }
            sample r#"{"type":"configure_applied","ignored_keys":["video.bitrate"]}"#;
        event ConfigureError { message: String, restart_required: bool }
            sample r#"{"type":"configure_error","message":"settings file not found","restart_required":false}"#;
        terminal Done {
            result: RecorderResult,
            code: i64,
            message: String,
            detail: Option<String>,
            tracks: Option<Tracks>,
            input_capture: Option<String>,
            window_capture: Option<String>,
        }
            sample r#"{"type":"done","result":"stopped","code":0,"message":"Successfully stopped","tracks":{"screen":{"index":0,"width":2560,"height":1440},"audio":[]},"input_capture":"/tmp/rec/input.jsonl"}"#;
        terminal Cameras { cameras: Vec<Camera> }
            sample r#"{"type":"cameras","cameras":[{"id":"0x1420000005ac8600","name":"Caméra intégrée <FaceTime> & 'HD' +1"}]}"#;
    }
    commands {
        Start {}
            sample r#"{"type":"start"}"#;
        Pause {}
            sample r#"{"type":"pause"}"#;
        MuteSpeaker { index: u32 }
            sample r#"{"type":"mute_speaker","index":1}"#;
        UnmuteSpeaker { index: u32 }
            sample r#"{"type":"unmute_speaker","index":1}"#;
        MuteMic { index: u32 }
            sample r#"{"type":"mute_mic","index":2}"#;
        UnmuteMic { index: u32 }
            sample r#"{"type":"unmute_mic","index":2}"#;
        Configure { path: String }
            sample r#"{"type":"configure","path":"C:\\Users\\me\\AppData\\Local\\Prisc\\recorder settings.json"}"#;
    }
}

// §4.2
ipc_catalog! {
    catalog share_region;
    helpers ["prisc_share_region"];
    quit_grace_ms 5000;
    strings ObscureMode { None, Blur, Pixelate, Hide }
    strings ShareRegionResult { Stopped, Cancelled }
    record Rect { x: i32, y: i32, w: u32, h: u32 }
    events {
        event Initialized {}
            sample r#"{"type":"initialized"}"#;
        event SharingStarted { region: Rect }
            sample r#"{"type":"sharing_started","region":{"x":-1920,"y":0,"w":1280,"h":720}}"#;
        event RegionChanged { region: Rect }
            sample r#"{"type":"region_changed","region":{"x":100,"y":200,"w":640,"h":480}}"#;
        event Obscure { mode: ObscureMode, strength: u32 }
            sample r#"{"type":"obscure","mode":"blur","strength":50}"#;
        event Status { fps: f64 }
            sample r#"{"type":"status","fps":29.5}"#;
        terminal Done { result: ShareRegionResult }
            sample r#"{"type":"done","result":"cancelled"}"#;
    }
    commands {
        Move { region: Rect }
            sample r#"{"type":"move","region":{"x":100,"y":200,"w":640,"h":480}}"#;
        Obscure { mode: ObscureMode, strength: Option<u32> }
            sample r#"{"type":"obscure","mode":"pixelate","strength":30}"#;
    }
}

// §4.3
ipc_catalog! {
    catalog job;
    helpers ["prisc_vid2gif", "Prisc.VideoRender"];
    quit_grace_ms 5000;
    strings JobResult { Ok, Cancelled }
    events {
        event Progress { percent: u32 }
            sample r#"{"type":"progress","percent":42}"#;
        terminal Done { result: JobResult, path: Option<String>, bytes: Option<u64> }
            sample r#"{"type":"done","result":"ok","path":"/tmp/a.gif","bytes":1024}"#;
    }
    commands {}
}

// §4.4
ipc_catalog! {
    catalog scroll;
    helpers ["prisc_scroll_driver"];
    quit_grace_ms 5000;
    strings ScrollState { Rewinding, Paused, Resuming, Scrolling, Settling, Stitching }
    strings ScrollResult { Complete, Stopped, MaxReached, NoMovement, Cancelled }
    events {
        event Ready {}
            sample r#"{"type":"ready"}"#;
        event Status { frames: u32, height_px: u32, state: ScrollState, resume_in_s: Option<u64> }
            sample r#"{"type":"status","frames":12,"height_px":4800,"state":"paused","resume_in_s":3}"#;
        terminal Done { result: ScrollResult, frames: u32, height_px: u32 }
            sample r#"{"type":"done","result":"max_reached","frames":40,"height_px":16000}"#;
    }
    commands {
        Stop {}
            sample r#"{"type":"stop"}"#;
    }
}

// §4.5
ipc_catalog! {
    catalog capture;
    helpers ["prisc_capture"];
    quit_grace_ms 5000;
    strings CaptureMode { Region, Screen | Monitor, Window }
    strings CaptureResult { Finished, Cancelled, Stopped }
    record Hotkeys { main: Vec<HotkeyStatus>, window: Vec<HotkeyStatus>, monitor: Vec<HotkeyStatus> }
    record HotkeyStatus { gesture: String, active: bool, error: Option<String>, error_code: Option<String> }
    events {
        event Ready {}
            sample r#"{"type":"ready"}"#;
        event Session { dir: String }
            sample r#"{"type":"session","dir":"/tmp/prisc/session-1"}"#;
        event CaptureFinished { dir: String }
            sample r#"{"type":"capture_finished","dir":"/tmp/prisc/session-1"}"#;
        event SettingsStatus { hotkeys: Hotkeys }
            sample r#"{"type":"settings_status","hotkeys":{"main":[{"gesture":"PrintScreen","active":true}],"window":[{"gesture":"Control+Shift+1","active":false,"error":"duplicate gesture","error_code":"duplicate"}],"monitor":[]}}"#;
        terminal Done { result: CaptureResult, dir: Option<String> }
            sample r#"{"type":"done","result":"finished","dir":"/tmp/prisc/session-1"}"#;
    }
    commands {
        Settings { args: Vec<String> }
            sample r#"{"type":"settings","args":["--theme","dark"]}"#;
        Capture { mode: CaptureMode }
            sample r#"{"type":"capture","mode":"window"}"#;
        Pause { paused: bool }
            sample r#"{"type":"pause","paused":true}"#;
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// Deserializes `sample` into `T`, checks its `type` is `name`, and re-serializes it byte for
    /// byte.
    pub(crate) fn check_sample<T: Serialize + DeserializeOwned>(name: &str, sample: &str) {
        let value: serde_json::Value = serde_json::from_str(sample).unwrap_or_else(|e| panic!("{sample}: {e}"));
        assert_eq!(value["type"], name, "{sample}: wrong type");
        let parsed: T = serde_json::from_str(sample).unwrap_or_else(|e| panic!("{sample}: {e}"));
        assert_eq!(serde_json::to_string(&parsed).unwrap(), sample);
    }

    #[test]
    fn samples_round_trip() {
        recorder::check_samples();
        share_region::check_samples();
        job::check_samples();
        scroll::check_samples();
        capture::check_samples();
        check_sample::<Hello>(HELLO.name, HELLO.sample);
        check_sample::<Quit>(QUIT.name, QUIT.sample);
    }

    #[test]
    fn names_are_unique_and_never_shadow_the_common_messages() {
        for spec in ALL {
            for (kind, messages, reserved) in [
                ("event", spec.events, ["hello", "error"]),
                ("command", spec.commands, ["quit", "quit"]),
            ] {
                for (i, message) in messages.iter().enumerate() {
                    assert!(
                        !reserved.contains(&message.name),
                        "{}: {kind} {} is common",
                        spec.name,
                        message.name
                    );
                    assert!(
                        messages[..i]
                            .iter()
                            .all(|m| m.name != message.name),
                        "{}: duplicate {kind} {}",
                        spec.name,
                        message.name
                    );
                }
            }
            for (i, strings) in spec.strings.iter().enumerate() {
                let names = spec.strings[..i]
                    .iter()
                    .map(|s| s.name)
                    .chain(spec.records.iter().map(|r| r.name));
                assert!(
                    names
                        .chain(["ErrorCode"])
                        .all(|n| n != strings.name),
                    "{}: duplicate type {}",
                    spec.name,
                    strings.name
                );
            }
            assert!(
                ALL.iter()
                    .filter(|other| other.name == spec.name)
                    .count()
                    == 1
            );
        }
    }

    #[test]
    fn terminal_flags_follow_the_spec() {
        let terminals = |spec: &CatalogSpec| {
            spec.events
                .iter()
                .filter(|e| e.terminal)
                .map(|e| e.name)
                .collect::<Vec<_>>()
        };
        assert_eq!(terminals(&recorder::SPEC), ["done", "cameras"]);
        assert_eq!(terminals(&share_region::SPEC), ["done"]);
        assert_eq!(terminals(&job::SPEC), ["done"]);
        assert_eq!(terminals(&scroll::SPEC), ["done"]);
        assert_eq!(terminals(&capture::SPEC), ["done"]);
        use super::Event as _;
        assert!(job::Event::Done {
            result: job::JobResult::Ok,
            path: None,
            bytes: None
        }
        .is_terminal());
        assert!(!job::Event::Progress {
            percent: 1
        }
        .is_terminal());
        assert!(job::Event::error(ErrorCode::Failed, "x".into(), true).is_terminal());
        assert!(!job::Event::error(ErrorCode::BadCommand, "x".into(), false).is_terminal());
    }

    #[test]
    fn quit_graces() {
        assert_eq!(recorder::QUIT_GRACE, Duration::from_secs(65));
        assert_eq!(recorder::SPEC.quit_grace(), recorder::QUIT_GRACE);
        for spec in [&share_region::SPEC, &job::SPEC, &scroll::SPEC, &capture::SPEC] {
            assert_eq!(spec.quit_grace(), Duration::from_secs(5), "{}", spec.name);
        }
    }

    #[test]
    fn options_are_omitted_and_missing_or_null_decode_as_none() {
        let done = job::Event::Done {
            result: job::JobResult::Cancelled,
            path: None,
            bytes: None,
        };
        assert_eq!(serde_json::to_string(&done).unwrap(), r#"{"type":"done","result":"cancelled"}"#);
        let parsed: job::Event = serde_json::from_str(r#"{"type":"done","result":"cancelled","path":null}"#).unwrap();
        assert_eq!(parsed, done);
    }

    #[test]
    fn unknown_fields_are_ignored_and_type_need_not_be_first() {
        let parsed: job::Event = serde_json::from_str(r#"{"percent":7,"extra":{"a":[1]},"type":"progress"}"#).unwrap();
        assert_eq!(
            parsed,
            job::Event::Progress {
                percent: 7
            }
        );
    }

    #[test]
    fn aliases_decode_only() {
        let parsed: capture::Command = serde_json::from_str(r#"{"type":"capture","mode":"monitor"}"#).unwrap();
        assert_eq!(
            parsed,
            capture::Command::Capture {
                mode: capture::CaptureMode::Screen
            }
        );
        assert_eq!(serde_json::to_string(&parsed).unwrap(), r#"{"type":"capture","mode":"screen"}"#);
        assert!(serde_json::from_str::<capture::Command>(r#"{"type":"capture","mode":"desktop"}"#).is_err());
        assert_eq!(capture::CaptureMode::SPEC.values[1].aliases, ["monitor"]);
    }

    #[test]
    fn wire_names_are_snake_case() {
        assert_eq!(scroll::ScrollResult::MaxReached.as_str(), "max_reached");
        assert_eq!(ErrorCode::InitFailed.as_str(), "init_failed");
        assert_eq!(recorder::SPEC.events[1].name, "started_recording");
        assert_eq!(recorder::SPEC.events[1].ident, "StartedRecording");
    }

    #[test]
    fn whole_number_floats_keep_their_point() {
        let levels = recorder::Event::Levels {
            speaker: vec![-100.0],
            mic: vec![],
        };
        assert_eq!(
            serde_json::to_string(&levels).unwrap(),
            r#"{"type":"levels","speaker":[-100.0],"mic":[]}"#
        );
    }

    #[test]
    fn hash_is_fnv1a_over_the_canonical_text() {
        // FNV-1a test vectors
        assert_eq!(fnv1a64(b""), 0xcbf2_9ce4_8422_2325);
        assert_eq!(fnv1a64(b"a"), 0xaf63_dc4c_8601_ec8c);
        let hashes: Vec<String> = ALL
            .iter()
            .map(|spec| spec.catalog_hash())
            .collect();
        for (i, hash) in hashes.iter().enumerate() {
            assert_eq!(hash.len(), 16);
            assert!(hash
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)));
            assert!(!hashes[..i].contains(hash));
        }
        let text = job::SPEC.canonical();
        assert!(text.starts_with("catalog job\nstrings ErrorCode bad_command rejected usage init_failed io_failed permission failed\n"));
        assert!(text.contains("\nevent done terminal result:JobResult path:Option<String> bytes:Option<u64>\n"));
        assert!(text.ends_with("\ncommand quit\n"));
        assert!(capture::SPEC
            .canonical()
            .contains("\nstrings CaptureMode region screen|monitor window\n"));
    }
}
