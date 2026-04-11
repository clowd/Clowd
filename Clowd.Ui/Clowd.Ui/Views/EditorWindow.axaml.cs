using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;
using Clowd.Ui.Models;
using Clowd.Ui.Services;
using Clowd.Ui.Views.Dialogs;

namespace Clowd.Ui.Views;

public partial class EditorWindow : Window
{
    private EditorSession _session;
    private readonly SessionStore? _sessions;

    public EditorWindow() : this(new EditorSession(), null)
    {
    }

    public EditorWindow(EditorSession session, SessionStore? sessions)
    {
        InitializeComponent();

        _session = session;
        _sessions = sessions;

        Title = $"Edit - {session.Name} - Clowd";

        // Hydrate the canvas from the session.
        Canvas.ArtworkBackground = session.Background != Colors.Transparent
            ? session.Background
            : (Application.Current is App app ? app.Settings.Editor.CanvasBackground : Colors.Transparent);

        foreach (var g in session.Graphics)
        {
            // System.Text.Json's IJsonOnDeserialized hook on GraphicBase has
            // already called Normalize during deserialize, so we can just add.
            Canvas.GraphicsList.Add(g);
        }

        // Sensible defaults for new shapes.
        Canvas.ObjectColor = Colors.Crimson;
        Canvas.LineWidth = 4;
        Canvas.Tool = ToolType.Pointer;
    }

    // ---- Tool selection ----

    private void OnToolButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string toolName } &&
            Enum.TryParse<ToolType>(toolName, out var tool))
        {
            Canvas.Tool = tool;
        }
    }

    // ---- Toolbar commands ----

    private void OnUndoClick(object? sender, RoutedEventArgs e) => Canvas.Undo();
    private void OnRedoClick(object? sender, RoutedEventArgs e) => Canvas.Redo();
    private void OnResetZoomClick(object? sender, RoutedEventArgs e) => Canvas.ResetViewport();

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_sessions == null)
        {
            await MessageDialog.ShowAsync(this, "Save", "This editor was opened without a session store.");
            return;
        }

        try
        {
            CaptureSessionFromCanvas();
            _sessions.Save(_session);
            await MessageDialog.ShowAsync(this, "Saved",
                $"Session '{_session.Name}' was saved.");
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(this, "Save failed", ex.Message);
        }
    }

    private void CaptureSessionFromCanvas()
    {
        _session.Background = Canvas.ArtworkBackground;
        _session.Graphics = Canvas.GraphicsList
            .Where(g => !g.IsScaffolding)
            .ToArray();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        await MessageDialog.ShowAsync(this, "Copy", "Copy to clipboard is not implemented yet.");
    }

    private async void OnUploadClick(object? sender, RoutedEventArgs e)
    {
        await MessageDialog.ShowAsync(this, "Upload", "Upload is not implemented yet.");
    }

    private async void OnFontClick(object? sender, RoutedEventArgs e)
    {
        await MessageDialog.ShowAsync(this, "Font", "Font picker is not implemented yet (Phase 12).");
    }

    // ---- Color pickers ----

    private async void OnCanvasBackgroundClick(object? sender, PointerPressedEventArgs e)
    {
        var dialog = new ColorPickerDialog(Canvas.ArtworkBackground);
        var result = await dialog.ShowDialogAsync(this);
        if (result.HasValue)
        {
            Canvas.ArtworkBackground = result.Value;
            // Persist back to settings.
            if (Application.Current is App app)
                app.Settings.Editor.CanvasBackground = result.Value;
        }
    }

    private async void OnObjectColorClick(object? sender, PointerPressedEventArgs e)
    {
        var dialog = new ColorPickerDialog(Canvas.ObjectColor);
        var result = await dialog.ShowDialogAsync(this);
        if (result.HasValue)
        {
            Canvas.ObjectColor = result.Value;
        }
    }
}
