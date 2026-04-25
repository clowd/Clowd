using Avalonia.Input;

namespace Clowd.Ui.Models.Settings;

public sealed class SettingsHotkey : CategoryBase
{
    private GlobalTrigger _fileUploadShortcut = new();
    private GlobalTrigger _clipboardUploadShortcut = new();
    private GlobalTrigger _captureRegionShortcut = new(Key.PrintScreen);
    private GlobalTrigger _captureFullscreenShortcut = new(Key.PrintScreen, KeyModifiers.Control);
    private GlobalTrigger _captureActiveShortcut = new(Key.PrintScreen, KeyModifiers.Alt);
    private GlobalTrigger _drawOnScreenShortcut = new(Key.PrintScreen, KeyModifiers.Control | KeyModifiers.Shift);
    private GlobalTrigger _startStopRecordingShortcut = new(Key.PrintScreen, KeyModifiers.Shift);

    public GlobalTrigger FileUploadShortcut
    {
        get => _fileUploadShortcut;
        set => SetWithSubscription(ref _fileUploadShortcut, value);
    }

    public GlobalTrigger ClipboardUploadShortcut
    {
        get => _clipboardUploadShortcut;
        set => SetWithSubscription(ref _clipboardUploadShortcut, value);
    }

    public GlobalTrigger CaptureRegionShortcut
    {
        get => _captureRegionShortcut;
        set => SetWithSubscription(ref _captureRegionShortcut, value);
    }

    public GlobalTrigger CaptureFullscreenShortcut
    {
        get => _captureFullscreenShortcut;
        set => SetWithSubscription(ref _captureFullscreenShortcut, value);
    }

    public GlobalTrigger CaptureActiveShortcut
    {
        get => _captureActiveShortcut;
        set => SetWithSubscription(ref _captureActiveShortcut, value);
    }

    public GlobalTrigger DrawOnScreenShortcut
    {
        get => _drawOnScreenShortcut;
        set => SetWithSubscription(ref _drawOnScreenShortcut, value);
    }

    public GlobalTrigger StartStopRecordingShortcut
    {
        get => _startStopRecordingShortcut;
        set => SetWithSubscription(ref _startStopRecordingShortcut, value);
    }

    public SettingsHotkey()
    {
        Subscribe(
            _fileUploadShortcut, _clipboardUploadShortcut, _captureRegionShortcut,
            _captureFullscreenShortcut, _captureActiveShortcut, _drawOnScreenShortcut,
            _startStopRecordingShortcut);
    }

    public override void OnLoaded()
    {
        Subscribe(
            _fileUploadShortcut, _clipboardUploadShortcut, _captureRegionShortcut,
            _captureFullscreenShortcut, _captureActiveShortcut, _drawOnScreenShortcut,
            _startStopRecordingShortcut);
    }
}
