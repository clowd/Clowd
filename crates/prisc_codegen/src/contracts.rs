//! The typed contract constants (`prisc_rust_core::contract`, DESIGN 3.3) as C# static partial
//! classes. Each output file renders one or more contract modules into one class; the values come
//! from each module's `items()`, so a `computed` entry reaches C# as a literal.
//!
//! Where a hand-written C# class already owns the constants' names (`TrayTokens`, `ClickHighlight`,
//! `MatteGenerator`, `ShareRegionGeometry`, `H264EncoderSettings`), the output is another part of
//! that `partial` class, so callers keep reading `ClickHighlight.DurationMs` and the value has one
//! home, in Rust. Such a part carries no class summary: the hand-written part documents the class.

use std::fmt::Write as _;
use std::path::Path;

use prisc_rust_core::contract::{self, Item, Value};

use crate::{cs, Output};

/// A contract module's `items()`.
type ItemsFn = fn() -> Vec<Item>;

/// Where one output's constants go.
struct Target {
    namespace: &'static str,
    class: &'static str,
    /// `public` or `internal`.
    access: &'static str,
    /// What follows the access modifier: `static partial class`, or `sealed partial class` for a
    /// hand-written class that is not static.
    kind: &'static str,
    /// The class summary; empty for a part of a hand-written class.
    summary: &'static str,
    /// `(module path for the docs, items)`.
    modules: &'static [(&'static str, ItemsFn)],
}

const STATIC: &str = "static partial class";

const DEFAULTS: Target = Target {
    namespace: "Prisc",
    class: "SharedDefaults",
    access: "public",
    kind: STATIC,
    summary: "Defaults and accent constants the Rust helpers and Prisc.Ui share.",
    modules: &[("defaults", contract::defaults::items), ("accent", contract::accent::items)],
};
const TELEMETRY: Target = Target {
    namespace: "Prisc",
    class: "TelemetryContract",
    access: "internal",
    kind: STATIC,
    summary: "The crash and error reporting contract every process shares.",
    modules: &[("telemetry", contract::telemetry::items)],
};
const TRAY: Target = Target {
    namespace: "Prisc.UI.Controls.Tray",
    class: "TrayTokens",
    access: "public",
    kind: STATIC,
    summary: "",
    modules: &[("tray", contract::tray::items)],
};
const CLICK_HIGHLIGHT: Target = Target {
    namespace: "Prisc.VideoSDK.Composition",
    class: "ClickHighlight",
    access: "public",
    kind: STATIC,
    summary: "",
    modules: &[("click_highlight", contract::click_highlight::items)],
};
const MATTE: Target = Target {
    namespace: "Prisc.VideoSDK.Ai",
    class: "MatteGenerator",
    access: "public",
    kind: STATIC,
    summary: "",
    modules: &[("matte", contract::matte::items)],
};
const SHARE_REGION: Target = Target {
    namespace: "Prisc.UI",
    class: "ShareRegionGeometry",
    access: "public",
    kind: STATIC,
    summary: "",
    modules: &[("share_region", contract::share_region::items)],
};
const H264: Target = Target {
    namespace: "Prisc.VideoSDK.Media",
    class: "H264EncoderSettings",
    access: "public",
    kind: "sealed partial class",
    summary: "",
    modules: &[("encoding", contract::encoding::items)],
};

macro_rules! renderer {
    ($name:ident, $target:ident) => {
        fn $name(_root: &Path) -> anyhow::Result<String> {
            render(&$target)
        }
    };
}
renderer!(render_defaults, DEFAULTS);
renderer!(render_telemetry, TELEMETRY);
renderer!(render_tray, TRAY);
renderer!(render_click_highlight, CLICK_HIGHLIGHT);
renderer!(render_matte, MATTE);
renderer!(render_share_region, SHARE_REGION);
renderer!(render_h264, H264);

const SOURCE: &str = "the contract tables in crates/prisc_rust_core/src/contract";

/// Every contract output.
pub static OUTPUTS: &[Output] = &[
    Output {
        name: "contract-defaults",
        path: "ui/Prisc.Shared/Generated/SharedDefaults.g.cs",
        source: SOURCE,
        render: render_defaults,
    },
    Output {
        name: "contract-telemetry",
        path: "ui/Prisc.Ui/Util/TelemetryContract.g.cs",
        source: SOURCE,
        render: render_telemetry,
    },
    Output {
        name: "contract-tray",
        path: "ui/Prisc.Ui/Controls/Tray/TrayTokens.g.cs",
        source: SOURCE,
        render: render_tray,
    },
    Output {
        name: "contract-click-highlight",
        path: "ui/Prisc.VideoSDK/Composition/ClickHighlight.g.cs",
        source: SOURCE,
        render: render_click_highlight,
    },
    Output {
        name: "contract-matte",
        path: "ui/Prisc.VideoSDK/Ai/MatteGenerator.g.cs",
        source: SOURCE,
        render: render_matte,
    },
    Output {
        name: "contract-share-region",
        path: "ui/Prisc.Ui/Video/ShareRegionGeometry.g.cs",
        source: SOURCE,
        render: render_share_region,
    },
    Output {
        name: "contract-h264",
        path: "ui/Prisc.VideoSDK/Media/H264EncoderSettings.g.cs",
        source: SOURCE,
        render: render_h264,
    },
];

/// One constant: `public const <type> <Name> = <literal>;`.
fn constant(item: &Item) -> String {
    let name = cs::pascal(item.name);
    let (ty, name, value) = match &item.value {
        Value::I64(v) if i32::try_from(*v).is_ok() => ("int", name, v.to_string()),
        Value::I64(v) => ("long", name, format!("{v}L")),
        Value::U32(v) => ("uint", name, format!("{v}u")),
        Value::F64(v) => ("double", name, cs::double(*v)),
        Value::Str(s) => ("string", name, cs::string_literal(s)),
        Value::Rgb(v) => ("uint", name, cs::hex_u32(*v)),
        Value::DurationMs(ms) => {
            let name = if name.ends_with("Ms") { name } else { format!("{name}Ms") };
            if *ms <= i32::MAX as u64 {
                ("int", name, ms.to_string())
            } else {
                ("long", name, format!("{ms}L"))
            }
        }
    };
    format!("public const {ty} {name} = {value};")
}

fn render(target: &Target) -> anyhow::Result<String> {
    let mut out = cs::header("contracts", SOURCE);
    let _ = writeln!(out, "namespace {}\n{{", target.namespace);
    out.push_str(&cs::summary(target.summary, "    "));
    let _ = writeln!(out, "    {} {} {}\n    {{", target.access, target.kind, target.class);
    let mut first = true;
    for (module, items) in target.modules {
        for item in items() {
            if !first {
                out.push('\n');
            }
            first = false;
            let doc = item.doc_text();
            let doc = if doc.is_empty() {
                format!("contract::{module}::{}", item.name)
            } else {
                doc
            };
            out.push_str(&cs::summary(&doc, "        "));
            let _ = writeln!(out, "        {}", constant(&item));
        }
    }
    out.push_str("    }\n}\n");
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn constants_render_as_literals() {
        let item = |value| Item {
            name: "SOME_VALUE",
            doc: &[],
            value,
        };
        assert_eq!(constant(&item(Value::Rgb(0x3B97D2))), "public const uint SomeValue = 0x3B97D2u;");
        assert_eq!(constant(&item(Value::I64(1 << 20))), "public const int SomeValue = 1048576;");
        assert_eq!(
            constant(&item(Value::I64(1 << 40))),
            "public const long SomeValue = 1099511627776L;"
        );
        assert_eq!(constant(&item(Value::F64(0.85))), "public const double SomeValue = 0.85;");
        assert_eq!(
            constant(&item(Value::Str("prisc@{0}"))),
            "public const string SomeValue = \"prisc@{0}\";"
        );
        assert_eq!(constant(&item(Value::DurationMs(400))), "public const int SomeValueMs = 400;");
        assert_eq!(constant(&item(Value::U32(7))), "public const uint SomeValue = 7u;");
    }

    #[test]
    fn every_target_renders_a_partial_class() {
        for output in OUTPUTS {
            let text = (output.render)(Path::new(".")).unwrap();
            assert!(text.contains(" partial class "), "{}", output.path);
        }
    }
}
