using System.ComponentModel;
using RT.Serialization;

namespace Clowd.Config;

public class SettingsHotkey : CategoryBase
{
    [DisplayName("Upload from File"), ClassifyIgnoreIfDefault]
    public SimpleKeyGesture FileUploadShortcut
    {
        get => _fileUploadShortcut;
        set => Set(ref _fileUploadShortcut, value);
    }

    [DisplayName("Upload Clipboard"), ClassifyIgnoreIfDefault]
    public SimpleKeyGesture ClipboardUploadShortcut
    {
        get => _clipboardUploadShortcut;
        set => Set(ref _clipboardUploadShortcut, value);
    }

    [DisplayName("Capture Region"), ClassifyIgnoreIfDefault]
    public SimpleKeyGesture CaptureRegionShortcut
    {
        get => _captureRegionShortcut;
        set => Set(ref _captureRegionShortcut, value);
    }

    [DisplayName("Capture Active Screen"), ClassifyIgnoreIfDefault]
    public SimpleKeyGesture CaptureFullscreenShortcut
    {
        get => _captureFullscreenShortcut;
        set => Set(ref _captureFullscreenShortcut, value);
    }

    [DisplayName("Capture Active Window"), ClassifyIgnoreIfDefault]
    public SimpleKeyGesture CaptureActiveShortcut
    {
        get => _captureActiveShortcut;
        set => Set(ref _captureActiveShortcut, value);
    }

    [DisplayName("Draw on Screen"), ClassifyIgnoreIfDefault]
    public SimpleKeyGesture DrawOnScreenShortcut
    {
        get => _drawOnScreenShortcut;
        set => Set(ref _drawOnScreenShortcut, value);
    }

    [DisplayName("Start / Stop Recording"), ClassifyIgnoreIfDefault]
    public SimpleKeyGesture StartStopRecordingShortcut
    {
        get => _startStopRecordingShortcut;
        set => Set(ref _startStopRecordingShortcut, value);
    }

    private SimpleKeyGesture _fileUploadShortcut = new();
    private SimpleKeyGesture _clipboardUploadShortcut = new();
    private SimpleKeyGesture _captureRegionShortcut = new(GestureKey.PrintScreen);
    private SimpleKeyGesture _captureFullscreenShortcut = new(GestureKey.PrintScreen, GestureModifierKeys.Control);
    private SimpleKeyGesture _captureActiveShortcut = new(GestureKey.PrintScreen, GestureModifierKeys.Alt);
    private SimpleKeyGesture _drawOnScreenShortcut = new(GestureKey.PrintScreen, GestureModifierKeys.Control | GestureModifierKeys.Shift);
    private SimpleKeyGesture _startStopRecordingShortcut = new(GestureKey.PrintScreen, GestureModifierKeys.Shift);
}
