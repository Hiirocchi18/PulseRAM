fn main() {
    let config = slint_build::CompilerConfiguration::new().with_style("fluent".into());
    slint_build::compile_with_config("ui/app.slint", config).expect("the Slint interface failed to compile");

    // Icon, version details and the DPI/compatibility manifest for PulseRAM.exe.
    let target_os = std::env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    let target_env = std::env::var("CARGO_CFG_TARGET_ENV").unwrap_or_default();
    let can_compile_resources = cfg!(windows) || target_env == "gnu";
    if target_os == "windows" && can_compile_resources {
        let mut resources = winresource::WindowsResource::new();
        resources.set_icon("PulseRAM.ico");
        resources.set_manifest_file("app.manifest");
        resources.set("ProductName", "PulseRAM");
        resources.set("FileDescription", "PulseRAM");
        resources.set("CompanyName", "Hiirocchi18");
        resources.set("LegalCopyright", "MIT License");
        resources.set("OriginalFilename", "PulseRAM.exe");
        resources.compile().expect("the Windows resources failed to compile");
    }
    println!("cargo:rerun-if-changed=PulseRAM.ico");
    println!("cargo:rerun-if-changed=app.manifest");
}
