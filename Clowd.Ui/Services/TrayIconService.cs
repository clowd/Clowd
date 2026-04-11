using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Clowd.Ui.Services;

/// <summary>
/// Wraps Avalonia's TrayIcon with a small NativeMenu (Open Clowd / Upload File / Exit).
/// Construct after the application has a MainWindow.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;

    public event EventHandler? UploadFileRequested;

    public TrayIconService(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;

        _trayIcon = new TrayIcon
        {
            ToolTipText = "Clowd",
            IsVisible = true,
            Menu = new NativeMenu(),
        };

        var openItem = new NativeMenuItem("Open Clowd");
        openItem.Click += (_, _) => ShowMainWindow();

        var uploadItem = new NativeMenuItem("Upload file...");
        uploadItem.Click += (_, _) => UploadFileRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => _lifetime.Shutdown();

        _trayIcon.Menu.Items.Add(openItem);
        _trayIcon.Menu.Items.Add(uploadItem);
        _trayIcon.Menu.Items.Add(new NativeMenuItemSeparator());
        _trayIcon.Menu.Items.Add(exitItem);

        _trayIcon.Clicked += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_lifetime.MainWindow is null) return;
        _lifetime.MainWindow.Show();
        if (_lifetime.MainWindow.WindowState == WindowState.Minimized)
            _lifetime.MainWindow.WindowState = WindowState.Normal;
        _lifetime.MainWindow.Activate();
    }

    public void Dispose()
    {
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }
}
