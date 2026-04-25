using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Clowd.Ui.Views.Dialogs;

public partial class FontPickerDialog : Window
{
    private readonly List<string> _allFamilies;
    private string? _selected;

    public FontPickerDialog() : this(null)
    {
    }

    public FontPickerDialog(string? initial)
    {
        InitializeComponent();

        // Enumerate system fonts. FontManager.SystemFonts is the canonical list;
        // each entry is a FontFamily and we display its Name.
        _allFamilies = FontManager.Current.SystemFonts
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FamilyList.ItemsSource = _allFamilies;

        if (!string.IsNullOrEmpty(initial))
        {
            _selected = initial;
            var index = _allFamilies.FindIndex(n => string.Equals(n, initial, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                FamilyList.SelectedIndex = index;
                FamilyList.ScrollIntoView(_allFamilies[index]);
            }
        }
    }

    public Task<string?> ShowDialogAsync(Window owner) => ShowDialog<string?>(owner);

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        var query = FilterBox.Text?.Trim() ?? string.Empty;
        FamilyList.ItemsSource = query.Length == 0
            ? _allFamilies
            : _allFamilies.Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnFamilySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (FamilyList.SelectedItem is string name)
        {
            _selected = name;
            PreviewText.FontFamily = new FontFamily(name);
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(_selected);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close((string?)null);
}
