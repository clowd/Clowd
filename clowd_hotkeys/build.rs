fn main() {
    // Keep CI paths out of the dylib's install name; Clowd.Ui opens it by absolute path anyway.
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("macos") {
        println!("cargo:rustc-cdylib-link-arg=-Wl,-install_name,@rpath/libclowd_hotkeys.dylib");
    }
}
