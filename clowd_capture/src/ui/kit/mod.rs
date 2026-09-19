//! Generic floating-tray kit: tokens -> tree -> arrange -> place -> paint.
//! The kit never names a feature: owners supply the nouns, the kit supplies
//! the shape.
//!
//! The five strata, each a module:
//!   * [`tokens`]: logical design values and [`tokens::Scale`], the one place
//!     DPI rounding lives;
//!   * [`tree`]: the retained widget tree an owner builds from integers;
//!   * [`arrange`]: taffy build, compute and flatten into a [`scene::Scene`]
//!     of placed nodes, with the kit's own snap rule;
//!   * [`place`]: anchor the scene beside a region on some bounds;
//!   * [`paint`] and [`hover`]: the scene as draw instances, and the per-id
//!     veil fade a renderer feeds them from.
//!
//! The scene is plain data and the only thing that crosses threads: the
//! taffy tree never leaves `arrange`.

pub mod arrange;
pub mod hover;
pub mod paint;
pub mod place;
pub mod scene;
pub mod tokens;
pub mod tree;

#[cfg(test)]
mod gate {
    /// Feature and owner nouns the kit must never contain, stored REVERSED
    /// so this file passes its own scan. Kit words instead: bounds, anchor,
    /// region, strip, chassis, emblem, item.
    const BANNED_REVERSED: &[&str] = &[
        "daolpu",
        "rco",
        "llorcs",
        "erutpac",
        "erahs",
        "oediv",
        "hcraes",
        "noitceles",
        "lenap",
        "tuodaer",
        "rotinom",
        "dnammoc",
        "stnenopmoc",
    ];

    #[test]
    fn kit_names_no_feature() {
        let banned: Vec<String> = BANNED_REVERSED
            .iter()
            .map(|w| w.chars().rev().collect())
            .collect();
        let dir = concat!(env!("CARGO_MANIFEST_DIR"), "/src/ui/kit");
        // The scan is not vacuous if it reached this file: the one that
        // holds the banned words (reversed) and so proves the tokenizer ran.
        let mut scanned_self = false;
        for entry in std::fs::read_dir(dir).unwrap() {
            let path = entry.unwrap().path();
            let src = std::fs::read_to_string(&path).unwrap();
            scanned_self |= path.ends_with("mod.rs");
            for (n, line) in src.lines().enumerate() {
                // Split on anything that is not part of an identifier, so
                // `clowd_rust_core` and `hit_test` stay single tokens.
                for word in line.split(|c: char| !c.is_ascii_alphanumeric() && c != '_') {
                    let w = word.to_ascii_lowercase();
                    assert!(!banned.contains(&w), "{}:{}: kit names a feature: {word:?}", path.display(), n + 1);
                }
            }
        }
        assert!(scanned_self, "the gate must scan its own file");
    }
}
