using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Clowd.Config;
using Clowd.Localization;
using DynamicData.Binding;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Windowing;
using System;

namespace Clowd.Avalonia;

public partial class MainWindow : AppWindow
{
    //public IntPtr Handle => PlatformImpl.Handle.Handle;

    public bool IsDarkTheme { get; private set; }

    private SolidColorBrush THEME_LIGHT_ACTIVE = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    private SolidColorBrush THEME_LIGHT_INACTIVE = new SolidColorBrush(Color.FromRgb(243, 243, 243));
    private SolidColorBrush THEME_DARK_ACTIVE = new SolidColorBrush(Color.FromRgb(20, 20, 20));
    private SolidColorBrush THEME_DARK_INACTIVE = new SolidColorBrush(Color.FromRgb(32, 32, 32));

    public MainWindow()
    {
        SettingsRoot.LoadDefault();
        SettingsRoot.Current.General.WhenPropertyChanged(g => g.Language).Subscribe(lang =>
        {
            Languages.SetLangauge(lang.Value);
        });

        TitleBar.ExtendsContentIntoTitleBar = true;
        this.Activated += MainWindow_Activated;
        this.Deactivated += MainWindow_Deactivated;
        InitializeComponent();
        var faTheme = AvaloniaLocator.Current.GetService<FluentAvaloniaTheme>();
        faTheme.PreferSystemTheme = true;
        IsDarkTheme = faTheme.RequestedTheme == FluentAvaloniaTheme.DarkModeString;
        UpdateBackground(true);
        Width = 900;
        Height = 700;
    }

    private void MainWindow_Deactivated(object sender, EventArgs e)
    {
        UpdateBackground(false);
    }

    private void MainWindow_Activated(object sender, EventArgs e)
    {
        UpdateBackground(true);
    }

    protected override void OnRequestedThemeChanged(FluentAvaloniaTheme sender, RequestedThemeChangedEventArgs args)
    {
        base.OnRequestedThemeChanged(sender, args);
        IsDarkTheme = args.NewTheme == FluentAvaloniaTheme.DarkModeString;
        UpdateBackground(IsActive);
    }

    private void UpdateBackground(bool isActive)
    {
        if (ActualTransparencyLevel >= WindowTransparencyLevel.AcrylicBlur && isActive)
        {
            BackgroundOverlay.Opacity = IsDarkTheme ? 0.875 : 0.8;
            BackgroundOverlay.Background = IsDarkTheme ? THEME_DARK_ACTIVE : THEME_LIGHT_ACTIVE;
        }
        else
        {
            BackgroundOverlay.Opacity = 1;
            BackgroundOverlay.Background = IsDarkTheme ? THEME_DARK_INACTIVE : THEME_LIGHT_INACTIVE;
        }
    }

    private void ThemeSwitchClicked(object sender, RoutedEventArgs e)
    {
        var faTheme = AvaloniaLocator.Current.GetService<FluentAvaloniaTheme>();
        faTheme.RequestedTheme = IsDarkTheme ? FluentAvaloniaTheme.LightModeString : FluentAvaloniaTheme.DarkModeString;
    }
}
