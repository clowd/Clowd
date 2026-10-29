//! The helper protocol's C# mirror (`ui/Prisc.Ipc/Generated/HelperMessages.g.cs`,
//! HELPER_PROTOCOL.md section 6), moved here from prisc_ipc's `src/csharp.rs`, plus the stdin
//! framing vectors both command loops run (`ui/Prisc.Ipc.Tests/Generated/FramingVectors.g.cs`,
//! from `prisc_ipc::framing`).
//!
//! The codecs are straight-line `Utf8JsonReader`/`Utf8JsonWriter` code over the hand-written
//! helpers in `ui/Prisc.Ipc/IpcJson.cs`: no reflection, so trim-safe.

use std::fmt::Write as _;

use prisc_ipc::catalog::{self, CatalogSpec, ErrorCode, FieldSpec, MessageSpec, TypeSpec, ERROR, HELLO, QUIT};
use prisc_ipc::framing::{self, Expected, Segment};
use prisc_ipc::lifetime::QuitReason;

use crate::{cs, Output};

const SOURCE: &str = "the catalogs in crates/prisc_ipc/src/catalog.rs";
const FRAMING_SOURCE: &str = "the golden vectors in crates/prisc_ipc/src/framing.rs";

/// The helper protocol mirror and the framing vectors.
pub static OUTPUTS: &[Output] = &[
    Output {
        name: "ipc",
        path: "ui/Prisc.Ipc/Generated/HelperMessages.g.cs",
        source: SOURCE,
        render,
    },
    Output {
        name: "ipc-framing",
        path: "ui/Prisc.Ipc.Tests/Generated/FramingVectors.g.cs",
        source: FRAMING_SOURCE,
        render: render_framing,
    },
];

/// The C# property for a field of the record or message `owner`. A property may not share its
/// enclosing type's name (`cameras { cameras }`), so such a field is renamed.
fn property(owner: &str, field: &FieldSpec) -> String {
    let name = cs::pascal(field.name);
    match (name == owner, field.ty) {
        (false, _) => name,
        (true, TypeSpec::Vec(_)) => "Items".to_owned(),
        (true, _) => "Value".to_owned(),
    }
}

/// Names and type lookups for one catalog. `None` is the common scope (`hello`).
struct Scope<'a> {
    spec: Option<&'a CatalogSpec>,
    /// `Recorder`, `ShareRegion`, ...
    prefix: String,
}

enum Named {
    ErrorCode,
    Strings(String),
    Record(String),
}

impl<'a> Scope<'a> {
    fn new(spec: Option<&'a CatalogSpec>) -> Self {
        Self {
            spec,
            prefix: spec
                .map(|spec| cs::pascal(spec.name))
                .unwrap_or_default(),
        }
    }

    /// The C# name of a catalog-local type: prefixed with the catalog, unless it already is
    /// (`VideoTrack` → `RecorderVideoTrack`, `JobResult` stays).
    fn type_name(&self, name: &str) -> String {
        if name.starts_with(&self.prefix) {
            name.to_owned()
        } else {
            format!("{}{name}", self.prefix)
        }
    }

    fn named(&self, name: &str) -> Named {
        if name == ErrorCode::SPEC.name {
            return Named::ErrorCode;
        }
        let spec = self
            .spec
            .unwrap_or_else(|| panic!("{name} outside a catalog"));
        if spec.strings.iter().any(|s| s.name == name) {
            Named::Strings(self.type_name(name))
        } else if spec.records.iter().any(|r| r.name == name) {
            Named::Record(self.type_name(name))
        } else {
            panic!("{}: unknown type {name}", spec.name)
        }
    }

    fn is_value_type(&self, ty: TypeSpec) -> bool {
        match ty {
            TypeSpec::Bool | TypeSpec::U32 | TypeSpec::U64 | TypeSpec::I32 | TypeSpec::I64 | TypeSpec::F64 => true,
            TypeSpec::Named(name) => matches!(self.named(name), Named::Strings(_)),
            TypeSpec::String | TypeSpec::Vec(_) | TypeSpec::Option(_) => false,
        }
    }

    fn cs_type(&self, ty: TypeSpec) -> String {
        match ty {
            TypeSpec::Bool => "bool".into(),
            TypeSpec::U32 => "uint".into(),
            TypeSpec::U64 => "ulong".into(),
            TypeSpec::I32 => "int".into(),
            TypeSpec::I64 => "long".into(),
            TypeSpec::F64 => "double".into(),
            TypeSpec::String => "string".into(),
            TypeSpec::Vec(inner) => format!("IReadOnlyList<{}>", self.cs_type(*inner)),
            TypeSpec::Option(inner) if self.is_value_type(*inner) => format!("{}?", self.cs_type(*inner)),
            TypeSpec::Option(inner) => self.cs_type(*inner),
            TypeSpec::Named(name) => match self.named(name) {
                Named::ErrorCode => "string".into(),
                Named::Strings(name) | Named::Record(name) => name,
            },
        }
    }

    /// A `JsonRead<T>` method group reading one value of `ty`.
    fn reader(&self, ty: TypeSpec) -> String {
        match ty {
            TypeSpec::Bool => "IpcJson.ReadBool".into(),
            TypeSpec::U32 => "IpcJson.ReadUInt32".into(),
            TypeSpec::U64 => "IpcJson.ReadUInt64".into(),
            TypeSpec::I32 => "IpcJson.ReadInt32".into(),
            TypeSpec::I64 => "IpcJson.ReadInt64".into(),
            TypeSpec::F64 => "IpcJson.ReadDouble".into(),
            TypeSpec::String => "IpcJson.ReadString".into(),
            TypeSpec::Named(name) => match self.named(name) {
                Named::ErrorCode => "IpcJson.ReadString".into(),
                Named::Strings(name) | Named::Record(name) => format!("Read{name}"),
            },
            TypeSpec::Vec(_) | TypeSpec::Option(_) => panic!("nested {ty} is not supported"),
        }
    }

    /// An expression reading the value `r` is on.
    fn read(&self, ty: TypeSpec) -> String {
        match ty {
            TypeSpec::Vec(inner) => format!("IpcJson.ReadList<{}>(ref r, {})", self.cs_type(*inner), self.reader(*inner)),
            TypeSpec::Option(inner) if self.is_value_type(*inner) => {
                format!("IpcJson.ReadOptional<{}>(ref r, {})", self.cs_type(*inner), self.reader(*inner))
            }
            // null reads as null for every reference type
            TypeSpec::Option(inner) => self.read(*inner),
            ty => format!("{}(ref r)", self.reader(ty)),
        }
    }

    /// Statements writing `expr` as a bare value through `w`.
    fn write_value(&self, ty: TypeSpec, expr: &str) -> String {
        match ty {
            TypeSpec::Bool => format!("w.WriteBooleanValue({expr});"),
            TypeSpec::U32 | TypeSpec::U64 | TypeSpec::I32 | TypeSpec::I64 => format!("w.WriteNumberValue({expr});"),
            TypeSpec::F64 => format!("IpcJson.WriteDoubleValue(w, {expr});"),
            TypeSpec::String => format!("w.WriteStringValue({expr} ?? \"\");"),
            TypeSpec::Named(name) => match self.named(name) {
                Named::ErrorCode => format!("w.WriteStringValue({expr} ?? \"\");"),
                Named::Strings(name) => format!("w.WriteStringValue({name}Name({expr}));"),
                Named::Record(name) => format!("Write{name}(w, {expr});"),
            },
            TypeSpec::Vec(inner) => format!(
                "w.WriteStartArray(); foreach (var item in {expr} ?? Array.Empty<{}>()) {{ {} }} w.WriteEndArray();",
                self.cs_type(*inner),
                self.write_value(*inner, "item")
            ),
            TypeSpec::Option(_) => panic!("nested {ty} is not supported"),
        }
    }

    /// Statements writing one field of `owner` (the variable holding the record or message).
    fn write_field(&self, out: &mut String, indent: &str, field: &FieldSpec, expr: &str) {
        let name = field.name;
        match field.ty {
            TypeSpec::Option(inner) if self.is_value_type(*inner) => {
                let value = self.write_value(*inner, &format!("{expr}.Value"));
                let _ = writeln!(out, "{indent}if ({expr}.HasValue) {{ w.WritePropertyName(\"{name}\"); {value} }}");
            }
            TypeSpec::Option(inner) => {
                let value = self.write_value(*inner, expr);
                let _ = writeln!(out, "{indent}if ({expr} != null) {{ w.WritePropertyName(\"{name}\"); {value} }}");
            }
            ty => {
                let _ = writeln!(out, "{indent}w.WritePropertyName(\"{name}\"); {}", self.write_value(ty, expr));
            }
        }
    }

    /// Positional parameters: `uint Percent, string Path`.
    fn parameters(&self, owner: &str, fields: &[FieldSpec]) -> String {
        fields
            .iter()
            .map(|field| format!("{} {}", self.cs_type(field.ty), property(owner, field)))
            .collect::<Vec<_>>()
            .join(", ")
    }

    /// Reads the object `r` was opened on into locals and returns `new <ctor>(...)`.
    fn decode_body(&self, out: &mut String, indent: &str, fields: &[FieldSpec], ctor: &str) {
        if fields.is_empty() {
            let _ = writeln!(out, "{indent}while (IpcJson.NextProperty(ref r, out _)) r.Skip();");
            let _ = writeln!(out, "{indent}return new {ctor}();");
            return;
        }
        for field in fields {
            let init = match field.ty {
                TypeSpec::Vec(inner) => format!("Array.Empty<{}>()", self.cs_type(*inner)),
                _ => "default".into(),
            };
            let _ = writeln!(out, "{indent}{} {} = {init};", self.cs_type(field.ty), cs::local(field.name));
        }
        let _ = writeln!(out, "{indent}while (IpcJson.NextProperty(ref r, out var name))");
        let _ = writeln!(out, "{indent}{{");
        let _ = writeln!(out, "{indent}    switch (name)");
        let _ = writeln!(out, "{indent}    {{");
        for field in fields {
            let _ = writeln!(
                out,
                "{indent}        case \"{}\": {} = {}; break;",
                field.name,
                cs::local(field.name),
                self.read(field.ty)
            );
        }
        let _ = writeln!(out, "{indent}        default: r.Skip(); break;");
        let _ = writeln!(out, "{indent}    }}");
        let _ = writeln!(out, "{indent}}}");
        let args = fields
            .iter()
            .map(|field| cs::local(field.name))
            .collect::<Vec<_>>()
            .join(", ");
        let _ = writeln!(out, "{indent}return new {ctor}({args});");
    }

    /// Writes `"type"` then every field of message `var`.
    fn encode_body(&self, out: &mut String, indent: &str, message: &MessageSpec, var: &str) {
        let _ = writeln!(out, "{indent}w.WriteString(\"type\", \"{}\");", message.name);
        for field in message.fields {
            self.write_field(out, indent, field, &format!("{var}.{}", property(message.ident, field)));
        }
    }
}

fn render(_root: &std::path::Path) -> anyhow::Result<String> {
    let mut out = String::new();
    out.push_str(&cs::header("ipc", SOURCE));
    out.push_str(
        r#"#nullable disable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Prisc.Ipc
{
"#,
    );
    render_common(&mut out);
    render_helper_names(&mut out);
    for spec in catalog::ALL {
        render_catalog(&mut out, spec);
    }
    render_helper_specs(&mut out);
    render_samples(&mut out);
    out.push_str("}\n");
    Ok(out)
}

fn render_common(out: &mut String) {
    let scope = Scope::new(None);
    out.push_str(
        "    /// <summary>The protocol constants prisc_ipc defines (HELPER_PROTOCOL.md §2.1, §2.5, §3.3).</summary>\n    \
         public static class IpcProtocol\n    {\n",
    );
    let _ = writeln!(
        out,
        "        /// <summary>The envelope version every <c>hello</c> carries (§2.5).</summary>\n        public const int Version = {};",
        prisc_ipc::PROTOCOL
    );
    let _ = writeln!(
        out,
        "        /// <summary>The longest line accepted in either direction, in UTF-8 bytes without the newline (§2.1).</summary>\n        \
         public const int MaxLineBytes = {};",
        prisc_ipc::MAX_LINE
    );
    let _ = writeln!(
        out,
        "        /// <summary>Added to a catalog's quit grace for a helper's own hard-exit timer, so a living parent's kill fires \
         first (§3.3).</summary>\n        public const int SelfExitMarginMs = {};",
        prisc_ipc::SELF_EXIT_MARGIN.as_millis()
    );
    for (name, text, doc) in framing::REASONS {
        out.push_str(&cs::summary(&format!("Command-line refusal (§2.4). {doc}"), "        "));
        let _ = writeln!(out, "        public const string {name} = {};", cs::string_literal(text));
    }
    out.push_str("    }\n\n");

    out.push_str("    /// <summary>Helper exit codes (HELPER_PROTOCOL.md §5).</summary>\n    public static class HelperExit\n    {\n");
    for (name, code) in prisc_ipc::exit::ALL {
        let _ = writeln!(out, "        public const int {} = {code};", cs::pascal(name));
    }
    out.push_str("    }\n\n");

    out.push_str("    /// <summary>The <c>error.code</c> values (HELPER_PROTOCOL.md §2.2).</summary>\n    public static class IpcErrorCodes\n    {\n");
    for value in ErrorCode::SPEC.values {
        let _ = writeln!(out, "        public const string {} = \"{}\";", value.ident, value.name);
    }
    out.push_str("    }\n\n");

    let _ = writeln!(out, "    /// <summary>The first line every line-protocol helper writes.</summary>");
    let _ = writeln!(
        out,
        "    public sealed record IpcHello({});\n",
        scope.parameters("IpcHello", HELLO.fields)
    );

    out.push_str(
        r#"    /// <summary>The messages common to every catalog: <c>hello</c> and <c>quit</c>.</summary>
    public static class IpcCodec
    {
        /// <summary>The <c>quit</c> command line.</summary>
"#,
    );
    let _ = writeln!(out, "        public const string QuitLine = \"{}\";\n", cs::escape(QUIT.sample));
    out.push_str(
        r#"        /// <summary>The line's <c>type</c>, or null when it has none. Throws <see cref="JsonException"/> on invalid JSON.</summary>
        public static string ReadType(string line) => IpcJson.ReadType(Encoding.UTF8.GetBytes(line));

        /// <summary>The <c>hello</c> line, or null when the line is another type.</summary>
        public static IpcHello DecodeHello(string line)
        {
            var utf8 = Encoding.UTF8.GetBytes(line);
"#,
    );
    let _ = writeln!(out, "            if (IpcJson.ReadType(utf8) != \"{}\") return null;", HELLO.name);
    out.push_str("            var r = IpcJson.Open(utf8);\n");
    scope.decode_body(out, "            ", HELLO.fields, "IpcHello");
    out.push_str("        }\n\n        public static string EncodeHello(IpcHello hello) => IpcJson.Write(w =>\n        {\n");
    scope.encode_body(
        out,
        "            ",
        &MessageSpec {
            ident: "IpcHello",
            ..HELLO
        },
        "hello",
    );
    out.push_str("        });\n    }\n\n");
}

fn render_helper_names(out: &mut String) {
    out.push_str(
        "    /// <summary>The executable stem of every helper the apps locate and spawn (<c>prisc_ipc::locate::helper_names</c>).</summary>\n    \
         public static class HelperNames\n    {\n",
    );
    for (name, stem) in prisc_ipc::locate::helper_names() {
        let _ = writeln!(out, "        public const string {name} = {};", cs::string_literal(stem));
    }
    out.push_str("    }\n\n");
}

fn render_catalog(out: &mut String, spec: &CatalogSpec) {
    let scope = Scope::new(Some(spec));
    let prefix = &scope.prefix;
    let _ = writeln!(out, "    // --- {} ({}) ---\n", spec.name, spec.helpers.join(", "));

    for strings in spec.strings {
        let name = scope.type_name(strings.name);
        let members: Vec<&str> = std::iter::once("Unknown")
            .chain(strings.values.iter().map(|v| v.ident))
            .collect();
        out.push_str(&cs::enum_inline(&name, &members, "    "));
        out.push('\n');
    }
    for record in spec.records {
        let name = scope.type_name(record.name);
        let _ = writeln!(
            out,
            "    public sealed record {name}({});\n",
            scope.parameters(&name, record.fields)
        );
    }

    for (kind, messages) in [("Event", spec.events), ("Command", spec.commands)] {
        let _ = writeln!(
            out,
            "    /// <summary>What <c>{}</c> helpers {}.</summary>",
            spec.name,
            if kind == "Event" { "write" } else { "read" }
        );
        let _ = writeln!(out, "    public abstract record {prefix}{kind}\n    {{");
        for message in messages {
            let parameters = scope.parameters(message.ident, message.fields);
            let parameters = if parameters.is_empty() {
                String::new()
            } else {
                format!("({parameters})")
            };
            let _ = writeln!(out, "        public sealed record {}{parameters} : {prefix}{kind};", message.ident);
        }
        if kind == "Event" {
            let _ = writeln!(
                out,
                "        public sealed record Error({}) : {prefix}Event, IIpcError;",
                scope.parameters("Error", ERROR.fields)
            );
        }
        out.push_str("    }\n\n");
    }

    let _ = writeln!(out, "    public static class {prefix}Codec\n    {{");
    for (kind, messages) in [("Event", spec.events), ("Command", spec.commands)] {
        let messages: Vec<&MessageSpec> = messages
            .iter()
            .chain((kind == "Event").then_some(&ERROR))
            .collect();
        let _ = writeln!(
            out,
            "        /// <summary>Null for an unknown <c>type</c>. Throws <see cref=\"JsonException\"/> on invalid JSON.</summary>"
        );
        let _ = writeln!(out, "        public static {prefix}{kind} Decode{kind}(string line)\n        {{");
        out.push_str("            var utf8 = Encoding.UTF8.GetBytes(line);\n            switch (IpcJson.ReadType(utf8))\n            {\n");
        for message in &messages {
            let _ = writeln!(
                out,
                "                case \"{}\":\n                {{\n                    var r = IpcJson.Open(utf8);",
                message.name
            );
            scope.decode_body(
                out,
                "                    ",
                message.fields,
                &format!("{prefix}{kind}.{}", message.ident),
            );
            out.push_str("                }\n");
        }
        out.push_str("                default:\n                    return null;\n            }\n        }\n\n");

        let _ = writeln!(
            out,
            "        public static string Encode{kind}({prefix}{kind} value) => IpcJson.Write(w =>\n        {{"
        );
        out.push_str("            switch (value)\n            {\n");
        for message in &messages {
            let _ = writeln!(out, "                case {prefix}{kind}.{} m:", message.ident);
            scope.encode_body(out, "                    ", message, "m");
            out.push_str("                    break;\n");
        }
        let _ = writeln!(
            out,
            "                default:\n                    throw new ArgumentException($\"not a {} {kind}: {{value}}\", nameof(value));",
            spec.name
        );
        out.push_str("            }\n        });\n\n");
    }

    for strings in spec.strings {
        let name = scope.type_name(strings.name);
        let _ = writeln!(
            out,
            "        private static {name} Read{name}(ref Utf8JsonReader r) => IpcJson.ReadString(ref r) switch\n        {{"
        );
        for value in strings.values {
            for wire in std::iter::once(&value.name).chain(value.aliases) {
                let _ = writeln!(out, "            \"{wire}\" => {name}.{},", value.ident);
            }
        }
        let _ = writeln!(out, "            _ => {name}.Unknown,\n        }};\n");
        let _ = writeln!(
            out,
            "        private static string {name}Name({name} value) => value switch\n        {{"
        );
        for value in strings.values {
            let _ = writeln!(out, "            {name}.{} => \"{}\",", value.ident, value.name);
        }
        out.push_str("            _ => throw new ArgumentOutOfRangeException(nameof(value), value, \"no wire string\"),\n        };\n\n");
    }
    for record in spec.records {
        let name = scope.type_name(record.name);
        let _ = writeln!(out, "        private static {name} Read{name}(ref Utf8JsonReader r)\n        {{");
        out.push_str("            if (!IpcJson.EnterObject(ref r)) return null;\n");
        scope.decode_body(out, "            ", record.fields, &name);
        out.push_str("        }\n\n");
        let _ = writeln!(
            out,
            "        private static void Write{name}(Utf8JsonWriter w, {name} value)\n        {{"
        );
        out.push_str("            if (value == null) { w.WriteNullValue(); return; }\n            w.WriteStartObject();\n");
        for field in record.fields {
            scope.write_field(out, "            ", field, &format!("value.{}", property(&name, field)));
        }
        out.push_str("            w.WriteEndObject();\n        }\n\n");
    }
    // drop the blank line after the last member
    out.truncate(out.trim_end().len());
    out.push_str("\n    }\n\n");
}

fn render_helper_specs(out: &mut String) {
    out.push_str("    public static partial class HelperSpecs\n    {\n");
    for (i, spec) in catalog::ALL.iter().enumerate() {
        let prefix = cs::pascal(spec.name);
        let terminals: Vec<String> = spec
            .events
            .iter()
            .filter(|e| e.terminal)
            .map(|e| format!("e is {prefix}Event.{}", e.ident))
            .chain(["e is IIpcError { Fatal: true }".to_owned()])
            .collect();
        if i > 0 {
            out.push('\n');
        }
        let _ = writeln!(
            out,
            "        public static readonly HelperSpec<{prefix}Event, {prefix}Command> {prefix} = new("
        );
        let _ = writeln!(
            out,
            "            catalog: \"{}\", catalogHash: \"{}\", quitGrace: TimeSpan.FromMilliseconds({}),",
            spec.name,
            spec.catalog_hash(),
            spec.quit_grace_ms
        );
        let _ = writeln!(
            out,
            "            decode: {prefix}Codec.DecodeEvent, encode: {prefix}Codec.EncodeCommand,"
        );
        let _ = writeln!(
            out,
            "            encodeEvent: {prefix}Codec.EncodeEvent, decodeCommand: {prefix}Codec.DecodeCommand,"
        );
        let _ = writeln!(out, "            isTerminal: e => {});", terminals.join(" || "));
    }
    out.push_str("    }\n\n");
}

fn render_samples(out: &mut String) {
    fn sample(out: &mut String, catalog: &str, name: &str, line: &str, round_trip: &str) {
        assert!(
            !line.contains("\"\"\"") && !line.starts_with('"') && !line.ends_with('"'),
            "{line} needs another C# literal form"
        );
        let _ = writeln!(
            out,
            "            new(\"{catalog}\", \"{name}\", \"\"\"{line}\"\"\", line => {round_trip}),"
        );
    }
    out.push_str(
        r#"    /// <summary>Every catalog sample (the golden lines in catalog.rs), each with the generated codec's
    /// decode-then-encode, for Prisc.Ipc.Tests.</summary>
    internal static class HelperSamples
    {
        public static readonly HelperSample[] All =
        [
"#,
    );
    sample(
        out,
        "common",
        "hello",
        HELLO.sample,
        "IpcCodec.EncodeHello(IpcCodec.DecodeHello(line))",
    );
    sample(
        out,
        "common",
        "quit",
        QUIT.sample,
        "IpcCodec.ReadType(line) == \"quit\" ? IpcCodec.QuitLine : null",
    );
    for spec in catalog::ALL {
        let prefix = cs::pascal(spec.name);
        for message in spec.events.iter().chain([&ERROR]) {
            let round_trip = format!("{prefix}Codec.EncodeEvent({prefix}Codec.DecodeEvent(line))");
            sample(out, spec.name, &format!("event {}", message.name), message.sample, &round_trip);
        }
        for message in spec.commands {
            let round_trip = format!("{prefix}Codec.EncodeCommand({prefix}Codec.DecodeCommand(line))");
            sample(out, spec.name, &format!("command {}", message.name), message.sample, &round_trip);
        }
    }
    out.push_str("        ];\n    }\n");
}

/// A C# string literal of `bytes` read as Latin-1 (one char per byte), every byte outside
/// printable ASCII escaped, so the bytes survive any editor and line-ending conversion.
fn latin1_literal(bytes: &[u8]) -> String {
    ascii_literal(
        &bytes
            .iter()
            .map(|&b| char::from(b))
            .collect::<String>(),
    )
}

/// A C# string literal with every character outside printable ASCII escaped.
fn ascii_literal(text: &str) -> String {
    let mut out = String::from("\"");
    for c in text.chars() {
        match c {
            '\\' => out.push_str("\\\\"),
            '"' => out.push_str("\\\""),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            ' '..='~' => out.push(c),
            c => {
                let _ = write!(out, "\\u{:04X}", c as u32);
            }
        }
    }
    out.push('"');
    out
}

fn render_framing(_root: &std::path::Path) -> anyhow::Result<String> {
    let mut out = cs::header("ipc", FRAMING_SOURCE);
    out.push_str(
        r#"#nullable disable
using System.IO;

namespace Prisc.Ipc.Tests
{
    /// <summary>What a stdin loop must do, in order (prisc_ipc::framing::Expected).</summary>
    internal enum FramingExpectKind
    {
        /// <summary>A command reached the helper; <c>Text</c> is the JSON it equals.</summary>
        Command,
        /// <summary>A line was refused with exactly <c>Text</c>.</summary>
        Malformed,
        /// <summary>A line was refused with a reason starting with <c>Text</c>.</summary>
        MalformedPrefix,
        /// <summary>The quit path fired (once) for a quit command.</summary>
        QuitCommand,
        /// <summary>The quit path fired (once) at stdin EOF.</summary>
        QuitStdinClosed,
    }

    internal sealed record FramingExpect(FramingExpectKind Kind, string Text = null);

    /// <summary>Stdin bytes: a Latin-1 string (one char per byte), or one byte repeated.</summary>
    internal sealed record FramingSegment(string Latin1, byte Repeat = 0, int Count = 0);

    /// <summary>One golden vector: bytes on stdin, and what the loop must do with them.</summary>
    internal sealed record FramingVector(string Name, FramingSegment[] Input, FramingExpect[] Expected)
    {
        public byte[] Bytes()
        {
            var bytes = new MemoryStream();
            foreach (var segment in Input)
            {
                if (segment.Latin1 != null)
                    foreach (var c in segment.Latin1)
                        bytes.WriteByte((byte)c);
                else
                    for (int i = 0; i < segment.Count; i++)
                        bytes.WriteByte(segment.Repeat);
            }
            return bytes.ToArray();
        }

        public override string ToString() => Name;
    }

    /// <summary>Every golden vector; the test vocabulary is <c>ping{n}</c> and <c>echo{text}</c>.</summary>
    internal static class FramingVectors
    {
        public static readonly FramingVector[] All =
        [
"#,
    );
    for vector in framing::golden_vectors() {
        let _ = writeln!(out, "            new({}, [", cs::string_literal(vector.name));
        for segment in &vector.input {
            match *segment {
                Segment::Bytes(bytes) => {
                    let _ = writeln!(out, "                new({}),", latin1_literal(bytes));
                }
                Segment::Repeat(byte, count) => {
                    let _ = writeln!(out, "                new(null, 0x{byte:02X}, {count}),");
                }
            }
        }
        out.push_str("            ], [\n");
        for expected in &vector.expected {
            let (kind, text) = match *expected {
                Expected::Command(json) => ("Command", Some(json)),
                Expected::Malformed(reason) => ("Malformed", Some(reason)),
                Expected::MalformedPrefix(prefix) => ("MalformedPrefix", Some(prefix)),
                Expected::Quit(QuitReason::Command) => ("QuitCommand", None),
                Expected::Quit(QuitReason::StdinClosed) => ("QuitStdinClosed", None),
                Expected::Quit(other) => anyhow::bail!("vector {} expects quit {other:?}, which no stdin loop fires", vector.name),
            };
            match text {
                Some(text) => {
                    let _ = writeln!(out, "                new(FramingExpectKind.{kind}, {}),", ascii_literal(text));
                }
                None => {
                    let _ = writeln!(out, "                new(FramingExpectKind.{kind}),");
                }
            }
        }
        out.push_str("            ]),\n");
    }
    out.push_str("        ];\n    }\n}\n");
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rendered_shapes() {
        let cs = render(std::path::Path::new(".")).unwrap();
        assert!(cs.contains("        public const int Version = 1;\n"));
        assert!(cs.contains("        public const int MaxLineBytes = 1048576;\n"));
        assert!(cs.contains("        public const int SelfExitMarginMs = 5000;\n"));
        assert!(cs.contains("        public const string OversizeReason = \"command line longer than 1048576 bytes\";\n"));
        assert!(cs.contains("        public const string UnknownCommandReason = \"unknown command '{0}'\";\n"));
        assert!(cs.contains("        public const string VideoRender = \"Prisc.VideoRender\";\n"));
        assert!(cs.contains("        public const string Ai = \"prisc_ai\";\n"));
        assert!(cs.contains("        public const int NoScreenPermission = 3;\n"));
        assert!(cs.contains("        public const int RustPanic = 101;\n"));
        assert!(cs.contains("        public const string BadCommand = \"bad_command\";\n"));
        assert!(cs.contains(
            "    public sealed record IpcHello(uint Protocol, string Catalog, string CatalogHash, string Helper, string Version, uint Pid);\n"
        ));
        assert!(cs.contains("    public enum JobResult { Unknown, Ok, Cancelled }\n"));
        assert!(cs.contains("        public sealed record Done(JobResult Result, string Path, ulong? Bytes) : JobEvent;\n"));
        assert!(cs.contains("        public sealed record Error(string Code, string Message, bool Fatal) : JobEvent, IIpcError;\n"));
        assert!(cs.contains("    public sealed record RecorderTracks(RecorderVideoTrack Screen, RecorderVideoTrack Webcam, IReadOnlyList<RecorderAudioTrack> Audio);\n"));
        assert!(cs.contains("        public sealed record Cameras(IReadOnlyList<RecorderCamera> Items) : RecorderEvent;\n"));
        assert!(cs.contains("        public sealed record Initialized : RecorderEvent;\n"));
        assert!(cs.contains("            \"monitor\" => CaptureMode.Screen,\n"));
        assert!(cs.contains(
            "            isTerminal: e => e is RecorderEvent.Done || e is RecorderEvent.Cameras || e is IIpcError { Fatal: true });\n"
        ));
        assert!(cs.contains(&format!("catalogHash: \"{}\"", catalog::job::SPEC.catalog_hash())));
        assert!(cs.ends_with("    }\n}\n") && !cs.contains('\r'));
    }

    #[test]
    fn framing_vectors_render_bytes_exactly() {
        let cs = render_framing(std::path::Path::new(".")).unwrap();
        assert!(cs.contains("            new(\"quit_then_read\", [\n"));
        assert!(cs.contains("                new(\"\\u00EF\\u00BB\\u00BF{\\\"type\\\":\\\"quit\\\",\\\"why\\\":\\\"done\\\"}\\r\\n\"),\n"));
        assert!(cs.contains("                new(null, 0x78, 1048577),\n"));
        assert!(cs
            .contains("                new(FramingExpectKind.Command, \"{\\\"type\\\":\\\"echo\\\",\\\"text\\\":\\\"a\\uFFFDb\\\"}\"),\n"));
        assert!(cs.contains("                new(FramingExpectKind.QuitStdinClosed),\n"));
        assert!(!cs.contains('\r') && cs.is_ascii());
    }
}
