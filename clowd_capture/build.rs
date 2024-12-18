fn main() {
    let version = env!("CARGO_PKG_VERSION");
    let ver = semver::Version::parse(&version).expect("Unable to parse ngbv output as semver version");
    let ver: u64 = ver.major << 48 | ver.minor << 32 | ver.patch << 16;

    println!("cargo:rustc-env=NGBV_VERSION={}", version);
    println!("cargo:rustc-env=NGBV_VERSION_U64={}", ver);

    #[cfg(target_os = "windows")]
    winres::WindowsResource::new()
        .set_manifest_file("app.manifest")
        .set_version_info(winres::VersionInfo::PRODUCTVERSION, ver)
        .set_version_info(winres::VersionInfo::FILEVERSION, ver)
        .set_icon("../assets/regular/regular.ico")
        .set("CompanyName", "Clowd")
        .set("ProductName", "Clowd")
        .set("ProductVersion", &version)
        .set("FileDescription", "Clowd screen capture and editor tool")
        .set("LegalCopyright", "Caelan Sayler (c) 2024")
        .compile()
        .unwrap();
}
