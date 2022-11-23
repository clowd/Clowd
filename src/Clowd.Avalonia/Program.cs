using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ReactiveUI;
using FluentAvalonia.UI.Windowing;
using System;

namespace Clowd.Avalonia;

internal class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace()
        .With(new Win32PlatformOptions
        {
            UseWindowsUIComposition = true,
        })
        .UseFAWindowing()
        .UseReactiveUI();
}
