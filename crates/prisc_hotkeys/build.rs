fn main() {
    // Keep CI paths out of the dylib's install name; Prisc.Ui opens it by absolute path anyway.
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("macos") {
        println!("cargo:rustc-cdylib-link-arg=-Wl,-install_name,@rpath/libprisc_hotkeys.dylib");
    }
}
