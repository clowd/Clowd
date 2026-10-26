//! A snapshot of where the key table started: every name Avalonia 12.1.2's `Key` enum accepted
//! (canonical and duplicate names) round-trips through `prisc_hotkeys::Key`, so settings saved by
//! builds that stored Avalonia names load unchanged. If the table drifts on purpose later, edit or
//! delete this test.

use prisc_hotkeys::{Key, Modifiers};

fn snapshot() -> serde_json::Value {
    let text = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/tests/avalonia-12.1.2-keys.json")).unwrap();
    serde_json::from_str(&text).unwrap()
}

#[test]
fn every_avalonia_key_name_round_trips() {
    let snapshot = snapshot();
    let entries = snapshot["key"].as_array().unwrap();
    assert_eq!(entries.len(), 223);
    let mut values = std::collections::BTreeSet::new();
    for entry in entries {
        let name = entry["name"].as_str().unwrap();
        let value = entry["value"].as_i64().unwrap() as i32;
        let to_string = entry["toString"].as_str().unwrap();
        let key = Key::from_i32(value).unwrap_or_else(|| panic!("no key with value {value} ({name})"));
        assert_eq!(Key::from_name(name), Some(key), "{name}");
        assert_eq!(key.name(), to_string, "{name}");
        values.insert(value);
    }
    assert_eq!(values.len(), 194);
}

#[test]
fn modifier_bits_match_avalonia() {
    let snapshot = snapshot();
    for entry in snapshot["modifiers"].as_array().unwrap() {
        let expected = match entry["name"].as_str().unwrap() {
            "None" => Modifiers::NONE,
            "Alt" => Modifiers::ALT,
            "Control" => Modifiers::CONTROL,
            "Shift" => Modifiers::SHIFT,
            "Meta" => Modifiers::META,
            other => panic!("unexpected modifier {other}"),
        };
        assert_eq!(expected.bits() as i64, entry["value"].as_i64().unwrap());
    }
}
