using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Clowd.Ui.Models.Common;
using Clowd.Ui.Services;

namespace Clowd.Ui.ViewModels;

public sealed class NavItem
{
    public required string Label { get; init; }
    public required Geometry Icon { get; init; }
    public required PageKey Page { get; init; }
}

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly NavigationService _navigation;
    private NavItem? _selectedItem;
    private Control? _currentView;

    public IReadOnlyList<NavItem> NavItems { get; }

    private static Geometry FindIcon(string key)
        => (Geometry)Application.Current!.Resources[key]!;

    public NavItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Set(ref _selectedItem, value) && value != null)
            {
                CurrentView = _navigation.Resolve(value.Page);
            }
        }
    }

    public Control? CurrentView
    {
        get => _currentView;
        private set => Set(ref _currentView, value);
    }

    public MainWindowViewModel(NavigationService navigation)
    {
        _navigation = navigation;

        NavItems = new[]
        {
            new NavItem { Label = "Recent",  Icon = FindIcon("IconRecent"),  Page = PageKey.Recent  },
            new NavItem { Label = "General", Icon = FindIcon("IconGeneral"), Page = PageKey.General },
            new NavItem { Label = "Hotkeys", Icon = FindIcon("IconHotkeys"), Page = PageKey.Hotkeys },
            new NavItem { Label = "Editor",  Icon = FindIcon("IconEditor"),  Page = PageKey.Editor  },
            new NavItem { Label = "Uploads", Icon = FindIcon("IconUploads"), Page = PageKey.Uploads },
            new NavItem { Label = "About",   Icon = FindIcon("IconAbout"),   Page = PageKey.About   },
        };

        SelectedItem = NavItems[1]; // start on General
    }
}
