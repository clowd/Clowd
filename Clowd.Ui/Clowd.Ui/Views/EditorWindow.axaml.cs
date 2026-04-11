using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;
using Clowd.Ui.Models;
using Clowd.Ui.Models.Upload;
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

        // In-place text editing is hosted by the window (not the library).
        Canvas.TextEditRequested += OnTextEditRequested;
        Canvas.ViewportChanged += (_, _) => PositionTextEditBox();

        // Space / Shift hold-to-pan. Tunneling so the temporary tool flip
        // happens regardless of which control inside the window has focus.
        // Mirrors the legacy WPF rootGrid_PreviewKeyDown / KeyUp behaviour.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent,   OnPreviewKeyUp,   RoutingStrategies.Tunnel);
    }

    // ---- Hold-to-pan (Space / Shift) ----

    private ToolType? _tempPanPrevTool;
    private Key _tempPanTriggerKey;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tempPanPrevTool != null) return;
        if (e.Key != Key.Space && e.Key != Key.LeftShift && e.Key != Key.RightShift)
            return;

        // Skip when a text-entry control has focus so Space still types into
        // NumericUpDown / TextBox / dialogs without flipping the tool.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        _tempPanPrevTool = Canvas.Tool;
        _tempPanTriggerKey = e.Key;
        Canvas.Tool = ToolType.None; // Pan
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (_tempPanPrevTool == null) return;
        if (e.Key != _tempPanTriggerKey) return;
        Canvas.Tool = _tempPanPrevTool.Value;
        _tempPanPrevTool = null;
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

    /// <summary>
    /// Exports the current artwork as a PNG, prompting the user for a location.
    /// </summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        using var rtb = Canvas.RenderArtworkToBitmap();
        if (rtb == null)
        {
            await MessageDialog.ShowAsync(this, "Nothing to save", "The canvas is empty.");
            return;
        }

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save artwork as PNG",
                SuggestedFileName = $"{_session.Name}.png",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
                },
            });

            if (file == null) return;

            await using var stream = await file.OpenWriteAsync();
            rtb.Save(stream);
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(this, "Save failed", ex.Message);
        }
    }

    /// <summary>
    /// Writes the current artwork PNG bytes onto the clipboard so the image
    /// can be pasted into Paint, Photoshop, browsers, etc. Windows apps pick
    /// up the "PNG" format.
    /// </summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        using var rtb = Canvas.RenderArtworkToBitmap();
        if (rtb == null)
        {
            await MessageDialog.ShowAsync(this, "Nothing to copy", "The canvas is empty.");
            return;
        }

        try
        {
            using var ms = new MemoryStream();
            rtb.Save(ms);
            var pngBytes = ms.ToArray();

            var clipboard = Clipboard;
            if (clipboard == null)
            {
                await MessageDialog.ShowAsync(this, "Copy failed", "Clipboard is unavailable.");
                return;
            }

            // Avalonia 12: the legacy DataObject + SetDataObjectAsync pair is
            // deprecated in favour of DataTransfer / DataTransferItem. The
            // lazy Func<byte[]?> overload avoids pinning the payload until
            // a consumer actually asks for it.
            var item = new DataTransferItem();
            // Windows / most image-aware apps recognise the "PNG" format name.
            item.Set(DataFormat.CreateBytesPlatformFormat("PNG"), () => pngBytes);
            // Linux / web browsers / some Mac apps look for the MIME type instead.
            item.Set(DataFormat.CreateBytesPlatformFormat("image/png"), () => pngBytes);

            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(this, "Copy failed", ex.Message);
        }
    }

    /// <summary>
    /// Flattens the artwork and hands it to the default upload provider.
    /// URL (or error) is shown in a dialog when it returns.
    /// </summary>
    private async void OnUploadClick(object? sender, RoutedEventArgs e)
    {
        using var rtb = Canvas.RenderArtworkToBitmap();
        if (rtb == null)
        {
            await MessageDialog.ShowAsync(this, "Nothing to upload", "The canvas is empty.");
            return;
        }

        if (Application.Current is not App app)
            return;

        var provider =
            app.Settings.Uploads.GetDefaultProvider(SupportedUploadType.Image) ??
            app.Settings.Uploads.GetDefaultProvider(SupportedUploadType.All);

        if (provider?.Provider is null)
        {
            await MessageDialog.ShowAsync(this, "No default provider",
                "Enable a provider and mark it as default in the Uploads page first.");
            return;
        }

        try
        {
            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;

            var fileName = $"{_session.Name}.png";
            var url = await provider.Provider.UploadAsync(ms, fileName, CancellationToken.None);
            await MessageDialog.ShowAsync(this, "Upload complete", $"Uploaded to:\n{url}");
        }
        catch (NotImplementedException)
        {
            await MessageDialog.ShowAsync(this, "Provider not implemented",
                $"The {provider.Provider.Name} provider is a placeholder in this build. Try Catbox.");
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(this, "Upload failed", ex.Message);
        }
    }

    /// <summary>
    /// Auto-saves the session to JSON on window close so reopening it via the
    /// Recent Sessions list restores the full canvas state.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_sessions == null) return;

        try
        {
            CaptureSessionFromCanvas();
            _sessions.Save(_session);
        }
        catch
        {
            // Intentionally swallow: closing the window should not be blocked
            // by a session save failure. The user still gets their PNG export.
        }
    }

    private void CaptureSessionFromCanvas()
    {
        _session.Background = Canvas.ArtworkBackground;
        _session.Graphics = Canvas.GraphicsList
            .Where(g => !g.IsScaffolding)
            .ToArray();
    }

    private async void OnFontClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new FontPickerDialog(Canvas.TextFontFamilyName);
        var chosen = await dialog.ShowDialogAsync(this);
        if (!string.IsNullOrEmpty(chosen))
        {
            Canvas.TextFontFamilyName = chosen;
            // Also push onto any currently selected text graphic.
            foreach (var g in Canvas.GraphicsList.SelectedItems)
            {
                if (g is GraphicText t)
                    t.FontName = chosen;
            }
        }
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

    // ---- In-place text editing ----

    // Overlay TextBox state. Null while no edit is in progress.
    private TextBox? _textEditBox;
    private GraphicText? _textEditTarget;
    private string? _textEditOriginal;

    private void OnTextEditRequested(object? sender, TextEditRequestedEventArgs e)
    {
        // Any currently-open edit commits first so the user doesn't lose work.
        if (_textEditBox != null) EndTextEdit(commit: true);

        _textEditTarget = e.Target;
        _textEditOriginal = e.Target.Body;

        var box = new TextBox
        {
            Text = e.Target.Body,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.Black,
            FontFamily = new FontFamily(e.Target.FontName),
            FontStyle = e.Target.FontStyle,
            FontWeight = e.Target.FontWeight,
            FontStretch = e.Target.FontStretch,
            RenderTransformOrigin = RelativePoint.Center,
        };

        box.KeyDown += OnTextEditKeyDown;
        box.LostFocus += OnTextEditLostFocus;
        box.TextChanged += OnTextEditTextChanged;

        _textEditBox = box;
        OverlayLayer.Children.Add(box);
        PositionTextEditBox();

        // Focus after the TextBox has been attached to the visual tree.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_textEditBox == null) return;
            _textEditBox.Focus();
            _textEditBox.SelectAll();
        });
    }

    private void OnTextEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndTextEdit(commit: false);
            e.Handled = true;
            return;
        }
        // Enter commits; Shift+Enter inserts a newline (AcceptsReturn handles that).
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            EndTextEdit(commit: true);
            e.Handled = true;
        }
    }

    private void OnTextEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_textEditBox != null) EndTextEdit(commit: true);
    }

    private void OnTextEditTextChanged(object? sender, TextChangedEventArgs e)
    {
        // Live-sync so the background rectangle grows as the user types.
        if (_textEditBox == null || _textEditTarget == null) return;
        _textEditTarget.Body = _textEditBox.Text ?? string.Empty;
        PositionTextEditBox();
    }

    private void EndTextEdit(bool commit)
    {
        if (_textEditBox == null || _textEditTarget == null) return;

        _textEditBox.KeyDown -= OnTextEditKeyDown;
        _textEditBox.LostFocus -= OnTextEditLostFocus;
        _textEditBox.TextChanged -= OnTextEditTextChanged;

        if (commit)
        {
            var newText = (_textEditBox.Text ?? string.Empty).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(newText))
            {
                // Brand new + empty → discard; pre-existing + empty → revert.
                if (string.IsNullOrEmpty(_textEditOriginal))
                    Canvas.GraphicsList.Remove(_textEditTarget);
                else
                    _textEditTarget.Body = _textEditOriginal;
            }
            else if (newText != _textEditOriginal)
            {
                _textEditTarget.Body = newText;
                Canvas.AddCommandToHistory(false);
            }
        }
        else
        {
            _textEditTarget.Body = _textEditOriginal ?? string.Empty;
        }

        _textEditTarget.Editing = false;
        OverlayLayer.Children.Remove(_textEditBox);
        _textEditBox = null;
        _textEditTarget = null;
        _textEditOriginal = null;

        Canvas.Focus();
        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// Re-lays out the overlay TextBox to match the target graphic's current
    /// position, size, font size, and rotation in screen coordinates. Called
    /// when editing starts, on every TextChanged, and whenever the canvas
    /// viewport changes (pan, zoom, reset).
    /// </summary>
    private void PositionTextEditBox()
    {
        if (_textEditBox == null || _textEditTarget == null) return;

        var g = _textEditTarget;
        var ub = g.UnrotatedBounds;

        // Transform content-space → screen-space.
        var screenLeft = ub.Left * Canvas.ContentScale + Canvas.ContentOffset.X;
        var screenTop  = ub.Top  * Canvas.ContentScale + Canvas.ContentOffset.Y;
        var screenW    = ub.Width  * Canvas.ContentScale;
        var screenH    = ub.Height * Canvas.ContentScale;

        var pad = GraphicText.TextPadding * Canvas.ContentScale;
        global::Avalonia.Controls.Canvas.SetLeft(_textEditBox, screenLeft + pad);
        global::Avalonia.Controls.Canvas.SetTop(_textEditBox, screenTop + pad);
        _textEditBox.Width  = Math.Max(12, screenW - pad * 2);
        _textEditBox.Height = Math.Max(12, screenH - pad * 2);
        _textEditBox.FontSize = g.FontSize * Canvas.ContentScale;

        // CenterOfRotation lives at the mid-point of UnrotatedBounds after
        // Normalize, which is exactly the center of the TextBox's layout
        // rect, so RenderTransformOrigin=Center produces the right pivot.
        _textEditBox.RenderTransform = g.Angle != 0
            ? new RotateTransform(g.Angle)
            : null;
    }
}
