# Translating Clowd

Clowd has no translation platform and no crowdsourcing. Locale files are plain `.resx` in this
folder, edited by hand or by an AI, and validated by unit tests. That is the whole workflow.

## File layout

| File | What it is |
| --- | --- |
| `Strings.resx` | The neutral (English) strings. **Source of truth** — every key is defined here first. |
| `Strings.<culture>.resx` | One translation, e.g. `Strings.de.resx`, `Strings.fr.resx`, `Strings.pt-BR.resx`. |
| `Loc.cs` | Runtime lookup, live language switching, available-language discovery. |
| `TExtension.cs` | The `{loc:T Key}` XAML markup extension. |

The .NET SDK compiles each `Strings.<culture>.resx` into a satellite assembly
(`<culture>/Clowd.Shared.resources.dll`) beside the executable. Clowd finds the languages it can
offer by scanning for those folders, so **adding a file is all that is needed** — no list to update,
no csproj edit, no code change.

At runtime a missing key falls back through the culture chain (`pt-BR` → `pt` → English), so a
partial translation degrades to English per string rather than breaking.

## Key conventions

Keys are `Area_Name` in PascalCase and **must be valid C# identifiers** (`^[A-Za-z][A-Za-z0-9_]*$`)
— a source generator turns each key into a property of exactly that name.

| Prefix | Surface |
| --- | --- |
| `Nav_` | Main window navigation items (suffix is the `SettingsPageTab` name) |
| `Main_` | Main window chrome outside the pages |
| `Tray_` | Tray icon menu |
| `General_`, `Update_`, `Recent_`, `Uploads_`, `Editor_`, `Video_`, `Color_`, `Font_`, `About_` | Their respective pages/windows |
| `Dialog_` | Shared dialog buttons (Ok / Yes / No / Close) |
| `Category_` | Settings category headers |
| `Upload_<Provider>_Desc` | Upload provider descriptions |
| `Capture_` | Strings handed to the Rust capture overlay |
| `<TypeName>_<PropertyName>` / `<EnumType>_<Member>` | Settings labels resolved by convention from the settings-control factory |

A `_Desc` suffix marks the longer caption shown under a setting.

**Never translate**: the product name *Clowd*, provider brand names (Azure, imgur, …), file
extensions, and hotkey key names.

## Placeholder rules

Placeholders are .NET composite-format indexes: `{0}`, `{1}`, … Rules:

- Use **exactly** the same set of indexes as the English string — no more, no fewer. A unit test
  fails the build otherwise.
- You **may** reorder them if the target language needs a different word order: `"{1} of {0}"` is
  fine.
- `{{` and `}}` are escaped literal braces; leave them as they are.
- Never translate the text *inside* braces.
- The `<comment>` on an entry says what each placeholder will contain. Read it.

The Rust capture overlay uses named placeholders instead (`{window}`, `{monitor}`, `{hex}`,
`{error}`) — those are substituted on the Rust side, so copy the token through verbatim.

## Length limits

Most of the UI measures text at runtime and grows to fit. Two places do not:

- **Capture overlay panel buttons** (`Capture_Btn*`: UPLOAD, EDIT, VIDEO, COPY, SAVE, RESET, EXIT)
  are drawn in a fixed 50 px cell. Keep them to **about 8 characters**; longer labels are clipped.
- **Navigation items** (`Nav_*`) sit in a 150 px sidebar. One or two short words.

Anything longer than its English source by more than roughly half is worth a second look.

## Adding a language

1. Copy `Strings.resx` to `Strings.<culture>.resx`, using a .NET culture name — `de`, `fr`,
   `pt-BR`. Prefer the neutral form (`de`) unless the translation is genuinely region-specific.
2. Translate the `<value>` elements **only**. Leave `name` attributes, `<comment>` elements, the
   `<xsd:schema>` block and the `<resheader>` entries exactly as they are.
3. Keep the file sorted by key, same order as `Strings.resx`, so diffs stay readable.
4. Run `dotnet test clowd_ui/Clowd.Shared.Tests`. The localisation tests check key parity,
   placeholder arity and key syntax.
5. Build and run — the new language shows up in the General settings language list automatically.

Removing a language is deleting its file.

## Updating an existing language

When English strings are added or changed, the parity test fails and names the keys. Fill them in,
re-run the tests.

## AI translation workflow

This is the intended way to produce and maintain a translation:

1. Give the model three things:
   - `Strings.resx` (the English source, including the `<comment>` elements — they are the
     translator context),
   - the target `Strings.<culture>.resx` as it stands today (or nothing, for a new language),
   - the failing `dotnet test` output, which lists the exact missing/orphan keys.
2. Ask it to produce the complete target file, with these instructions:
   - translate `<value>` only; preserve every `name` attribute and `<comment>` verbatim,
   - preserve the placeholder index set of each English string,
   - honour the length limits above,
   - keep product and brand names untranslated,
   - use the register of a desktop utility: short, plain, imperative for buttons.
3. Save the result, run `dotnet test clowd_ui/Clowd.Shared.Tests`, and feed any failure message
   back to the model verbatim. Repeat until green.
4. Skim the result in the running app before committing. The tests prove the file is *structurally*
   correct; they cannot tell you a word is wrong.
