using System.Collections.Generic;
using System.Reflection;

namespace Clowd.Ui.ViewModels.Pages;

public sealed class DependencyInfo
{
    public required string Name { get; init; }
    public required string Url { get; init; }
}

public sealed class AboutViewModel
{
    public string Version { get; }

    public string Description =>
        "A screen capturing tool that has everything you need without endless tweaking. " +
        "Pixel-perfect selection, fast recording, image editing and easy sharing.";

    public string GitHubUrl => Constants.GitHubUrl;

    public IReadOnlyList<DependencyInfo> Dependencies { get; } = new[]
    {
        new DependencyInfo { Name = "Avalonia",        Url = "https://avaloniaui.net/" },
        new DependencyInfo { Name = ".NET",            Url = "https://dotnet.microsoft.com/" },
        new DependencyInfo { Name = "Inter font",      Url = "https://rsms.me/inter/" },
        new DependencyInfo { Name = "wgpu",            Url = "https://wgpu.rs/" },
        new DependencyInfo { Name = "winit",           Url = "https://github.com/rust-windowing/winit" },
        new DependencyInfo { Name = "image (Rust)",    Url = "https://github.com/image-rs/image" },
        new DependencyInfo { Name = "Tauri",           Url = "https://tauri.app/" },
        new DependencyInfo { Name = "tldraw",          Url = "https://www.tldraw.com/" },
    };

    public AboutViewModel()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Version = version is null ? "v?" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }
}
