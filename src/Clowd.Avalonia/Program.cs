using System;
using Avalonia;
using Avalonia.ReactiveUI;
using Clowd.Avalonia.Services;
using Clowd.Avalonia.ViewModels;
using Clowd.Config;
using Clowd.PlatformUtil.Windows;
using FluentAvalonia.UI.Windowing;

namespace Clowd.Avalonia;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        SettingsRoot.LoadDefault();
        ThreadDpiScalingContext.SetCurrentThreadScalingMode(ThreadScalingMode.PerMonitorV2Aware);

        var app = BuildAvaloniaApp();
        app.StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace()
        .With(new Win32PlatformOptions { UseWindowsUIComposition = true })
        .With(new AppUpdateViewModel())
        .With(new HotkeyRegistrationService())
        //.With(new HotkeysViewModel())
        .UseFAWindowing()
        .UseReactiveUI();
}
