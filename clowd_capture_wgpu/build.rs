fn main() {
    println!("cargo:rerun-if-changed=app.manifest");
    if std::env::var("CARGO_CFG_WINDOWS").is_ok() {
        let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
        let manifest_path = format!("{}/app.manifest", manifest_dir);
        println!("cargo:rustc-link-arg-bins=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bins=/MANIFESTINPUT:{}", manifest_path);
    }
}
