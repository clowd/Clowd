//! Typed constant tables that Rust and C# must agree on, declared once.
//!
//! Each submodule is one `contract!` block. The block expands to the items the Rust code uses
//! (`pub const` for a literal, a `LazyLock` static for a `computed` entry) and to a
//! `pub fn items() -> Vec<Item>` that prisc_codegen calls to render the same values as C#
//! constants (`cargo codegen`). A computed value (the shipped accent after the contrast rule, a
//! shifted size, a duration) is therefore evaluated in Rust at generation time and reaches C# as a
//! literal, never as a native call in a static initializer.
//!
//! ```ignore
//! crate::contract! {
//!     /// The accent the app ships with (sRGB 0x00RRGGBB).
//!     pub const PRISC_BLUE: Rgb = Rgb(0x3B97D2);
//!     /// The shipped accent after the contrast rule.
//!     computed DEFAULT_ACCENT: Rgb = crate::color::ensure_contrast_with_white(PRISC_BLUE, 4.5);
//! }
//! ```
//!
//! Which C# class a module lands in is prisc_codegen's table (`src/contracts.rs`).

use std::time::Duration;

pub mod accent;
pub mod click_highlight;
pub mod defaults;
pub mod encoding;
pub mod matte;
pub mod share_region;
pub mod telemetry;
pub mod tray;

/// An sRGB colour as `0x00RRGGBB`. Rendered in C# as a `uint` constant (`0x3B97D2u`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct Rgb(pub u32);

/// One contract value, as prisc_codegen renders it.
#[derive(Clone, Debug, PartialEq)]
pub enum Value {
    /// A C# `long` (an `int` when it fits is the renderer's choice).
    I64(i64),
    /// A C# `uint`.
    U32(u32),
    /// A C# `double`.
    F64(f64),
    /// A C# `string`.
    Str(&'static str),
    /// A C# `uint` colour, rendered in hex.
    Rgb(u32),
    /// A duration, rendered as whole milliseconds.
    DurationMs(u64),
}

/// One named contract entry: its Rust name (`SCREAMING_CASE`), its doc lines and its value.
#[derive(Clone, Debug, PartialEq)]
pub struct Item {
    pub name: &'static str,
    /// The `///` lines as the compiler sees them (each usually starts with a space).
    pub doc: &'static [&'static str],
    pub value: Value,
}

impl Item {
    /// The doc lines trimmed and joined with single spaces.
    pub fn doc_text(&self) -> String {
        self.doc
            .iter()
            .map(|line| line.trim())
            .filter(|line| !line.is_empty())
            .collect::<Vec<_>>()
            .join(" ")
    }
}

/// How a Rust value becomes a [`Value`]. Implemented for every type a contract may declare.
pub trait IntoValue {
    fn into_value(self) -> Value;
}

impl IntoValue for i32 {
    fn into_value(self) -> Value {
        Value::I64(self.into())
    }
}

impl IntoValue for i64 {
    fn into_value(self) -> Value {
        Value::I64(self)
    }
}

impl IntoValue for u32 {
    fn into_value(self) -> Value {
        Value::U32(self)
    }
}

impl IntoValue for f32 {
    fn into_value(self) -> Value {
        // Through the shortest decimal text, so 0.18f32 renders as 0.18 rather than
        // 0.18000000715255737.
        Value::F64(
            self.to_string()
                .parse()
                .unwrap_or(self as f64),
        )
    }
}

impl IntoValue for f64 {
    fn into_value(self) -> Value {
        Value::F64(self)
    }
}

impl IntoValue for &'static str {
    fn into_value(self) -> Value {
        Value::Str(self)
    }
}

impl IntoValue for Rgb {
    fn into_value(self) -> Value {
        Value::Rgb(self.0)
    }
}

impl IntoValue for Duration {
    fn into_value(self) -> Value {
        Value::DurationMs(self.as_millis() as u64)
    }
}

/// Declares a contract module's items; see the module docs. Two entry forms, each with its `///`
/// docs: `pub const NAME: Type = literal-or-const-expr;` and `computed NAME: Type = expr;` (any
/// expression, evaluated once on first use and at generation time).
#[macro_export]
macro_rules! contract {
    (@decl) => {};
    (@decl $(#[doc = $doc:literal])* pub const $name:ident : $ty:ty = $value:expr; $($rest:tt)*) => {
        $(#[doc = $doc])*
        pub const $name: $ty = $value;
        $crate::contract!(@decl $($rest)*);
    };
    (@decl $(#[doc = $doc:literal])* computed $name:ident : $ty:ty = $value:expr; $($rest:tt)*) => {
        $(#[doc = $doc])*
        pub static $name: ::std::sync::LazyLock<$ty> = ::std::sync::LazyLock::new(|| $value);
        $crate::contract!(@decl $($rest)*);
    };
    (@item $items:ident;) => {};
    (@item $items:ident; $(#[doc = $doc:literal])* pub const $name:ident : $ty:ty = $value:expr; $($rest:tt)*) => {
        $items.push($crate::contract::Item {
            name: stringify!($name),
            doc: &[$($doc),*],
            value: $crate::contract::IntoValue::into_value($name),
        });
        $crate::contract!(@item $items; $($rest)*);
    };
    (@item $items:ident; $(#[doc = $doc:literal])* computed $name:ident : $ty:ty = $value:expr; $($rest:tt)*) => {
        $items.push($crate::contract::Item {
            name: stringify!($name),
            doc: &[$($doc),*],
            value: $crate::contract::IntoValue::into_value(::std::clone::Clone::clone(&*$name)),
        });
        $crate::contract!(@item $items; $($rest)*);
    };
    ($($body:tt)*) => {
        $crate::contract!(@decl $($body)*);

        /// Every entry of this contract, in declaration order, for prisc_codegen.
        #[allow(unused_mut, clippy::vec_init_then_push)]
        pub fn items() -> ::std::vec::Vec<$crate::contract::Item> {
            let mut items = ::std::vec::Vec::new();
            $crate::contract!(@item items; $($body)*);
            items
        }
    };
}

#[cfg(test)]
mod tests {
    mod sample {
        use crate::contract::Rgb;
        use std::time::Duration;

        crate::contract! {
            /// A literal colour.
            pub const BLUE: Rgb = Rgb(0x3B97D2);
            /// A literal size
            /// on two lines.
            pub const SIDE: i32 = 1 << 4;
            pub const RATIO: f32 = 0.18;
            pub const NAME: &str = "prisc@{0}";
            pub const MARGIN: Duration = Duration::from_secs(5);
            /// Computed from the others.
            computed DOUBLE_SIDE: i64 = SIDE as i64 * 2;
        }
    }

    mod empty {
        crate::contract! {}
    }

    use super::{Item, Value};

    #[test]
    fn items_render_every_entry_in_order_with_docs_and_values() {
        let items = sample::items();
        let names: Vec<&str> = items.iter().map(|i| i.name).collect();
        assert_eq!(names, ["BLUE", "SIDE", "RATIO", "NAME", "MARGIN", "DOUBLE_SIDE"]);
        assert_eq!(items[0].value, Value::Rgb(0x3B97D2));
        assert_eq!(items[0].doc_text(), "A literal colour.");
        assert_eq!(items[1].value, Value::I64(16));
        assert_eq!(items[1].doc_text(), "A literal size on two lines.");
        assert_eq!(items[2].value, Value::F64(0.18));
        assert_eq!(items[2].doc_text(), "");
        assert_eq!(items[3].value, Value::Str("prisc@{0}"));
        assert_eq!(items[4].value, Value::DurationMs(5000));
        assert_eq!(items[5].value, Value::I64(32));
        assert_eq!(*sample::DOUBLE_SIDE, 32);
        assert_eq!(sample::SIDE, 16);
    }

    #[test]
    fn an_empty_contract_has_no_items() {
        assert_eq!(empty::items(), Vec::<Item>::new());
    }

    #[test]
    fn every_shipped_module_renders() {
        for items in [
            super::accent::items(),
            super::click_highlight::items(),
            super::defaults::items(),
            super::encoding::items(),
            super::matte::items(),
            super::share_region::items(),
            super::telemetry::items(),
            super::tray::items(),
        ] {
            for item in items {
                assert!(!item.name.is_empty());
            }
        }
    }
}
