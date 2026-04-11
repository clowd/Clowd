using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Clowd.Ui.Models.Settings;
using Clowd.Ui.Models.Upload;
using Clowd.Ui.Services;
using Clowd.Ui.ViewModels;
using Clowd.Ui.ViewModels.Pages;
using Clowd.Ui.Views;
using Clowd.Ui.Views.Dialogs;

namespace Clowd.Ui;

public partial class App : Application
{
    public SettingsRoot Settings { get; private set; } = null!;
    public IPlatformService Platform { get; private set; } = new DefaultPlatformService();

    private TrayIconService? _tray;
    private HotkeyBinder? _hotkeyBinder;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = SettingsRoot.Load();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray app: closing the main window hides it instead of quitting.
            // The app exits only when the tray "Exit" item calls Shutdown().
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var navigation = new NavigationService();
            navigation.Register(PageKey.Recent,  () => new RecentSessionsView());
            navigation.Register(PageKey.General, () => new GeneralSettingsView(Settings.General));
            navigation.Register(PageKey.Hotkeys, () => new HotkeysSettingsView(new HotkeysViewModel(Settings.Hotkeys)));
            navigation.Register(PageKey.Editor,  () => new EditorSettingsView(new EditorSettingsViewModel(Settings.Editor)));
            navigation.Register(PageKey.Uploads, () => new UploadSettingsView(Settings.Uploads));
            navigation.Register(PageKey.About,   () => new AboutView());

            var vm = new MainWindowViewModel(navigation);
            desktop.MainWindow = new MainWindow(vm);

            _tray = new TrayIconService(desktop);
            _tray.UploadFileRequested += async (_, _) => await HandleUploadFileFromTrayAsync();

            _hotkeyBinder = new HotkeyBinder(Platform);
            BindHotkeys();

            desktop.Exit += (_, _) =>
            {
                _hotkeyBinder?.Dispose();
                (Platform as IDisposable)?.Dispose();
                Settings.FlushAndDispose();
                _tray?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void BindHotkeys()
    {
        if (_hotkeyBinder is null) return;

        var keys = Settings.Hotkeys;
        _hotkeyBinder.Bind(keys.FileUploadShortcut, () => _ = HandleUploadFileFromTrayAsync());
        _hotkeyBinder.Bind(keys.ClipboardUploadShortcut, () => _ = ShowHotkeyStubAsync("Clipboard Upload"));
        _hotkeyBinder.Bind(keys.CaptureRegionShortcut, () => _ = ShowHotkeyStubAsync("Capture Region"));
        _hotkeyBinder.Bind(keys.CaptureFullscreenShortcut, () => _ = ShowHotkeyStubAsync("Capture Fullscreen"));
        _hotkeyBinder.Bind(keys.CaptureActiveShortcut, () => _ = ShowHotkeyStubAsync("Capture Active Window"));
        _hotkeyBinder.Bind(keys.DrawOnScreenShortcut, () => _ = ShowHotkeyStubAsync("Draw On Screen"));
        _hotkeyBinder.Bind(keys.StartStopRecordingShortcut, () => _ = ShowHotkeyStubAsync("Start/Stop Recording"));
    }

    private async System.Threading.Tasks.Task ShowHotkeyStubAsync(string commandName)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var window = desktop.MainWindow;
        if (window is null) return;

        if (!window.IsVisible)
        {
            window.Show();
            window.Activate();
        }

        await MessageDialog.ShowAsync(window, "Hotkey triggered",
            $"'{commandName}' pressed — action not yet implemented.");
    }

    private async System.Threading.Tasks.Task HandleUploadFileFromTrayAsync()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var window = desktop.MainWindow;
        if (window is null) return;

        var picked = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Upload file",
            AllowMultiple = false,
        });

        if (picked.Count == 0) return;
        var file = picked[0];

        var defaultProvider = Settings.Uploads.GetDefaultProvider(SupportedUploadType.All)
                              ?? Settings.Uploads.GetDefaultProvider(SupportedUploadType.Binary);

        if (defaultProvider?.Provider is null)
        {
            await MessageDialog.ShowAsync(window, "No default provider",
                "Enable a provider and mark it as default in the Uploads page first.");
            return;
        }

        try
        {
            await using var stream = await file.OpenReadAsync();
            var url = await defaultProvider.Provider.UploadAsync(stream, file.Name, default);
            await MessageDialog.ShowAsync(window, "Upload complete", $"Uploaded to:\n{url}");
        }
        catch (NotImplementedException)
        {
            await MessageDialog.ShowAsync(window, "Provider not implemented",
                $"The {defaultProvider.Provider.Name} provider is a placeholder in this build. Try Catbox.");
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(window, "Upload failed", ex.Message);
        }
    }
}
