using System.Collections.Generic;
using Clowd.Ui.Models.Settings;

namespace Clowd.Ui.ViewModels.Pages;

public sealed class HotkeyEntry
{
    public required string Label { get; init; }
    public required GlobalTrigger Trigger { get; init; }
}

public sealed class HotkeysViewModel
{
    public IReadOnlyList<HotkeyEntry> Entries { get; }

    public HotkeysViewModel(SettingsHotkey settings)
    {
        Entries = new[]
        {
            new HotkeyEntry { Label = "Capture region",       Trigger = settings.CaptureRegionShortcut },
            new HotkeyEntry { Label = "Capture active window",Trigger = settings.CaptureActiveShortcut },
            new HotkeyEntry { Label = "Capture full screen",  Trigger = settings.CaptureFullscreenShortcut },
            new HotkeyEntry { Label = "Upload clipboard",     Trigger = settings.ClipboardUploadShortcut },
            new HotkeyEntry { Label = "Upload from file",     Trigger = settings.FileUploadShortcut },
            new HotkeyEntry { Label = "Draw on screen",       Trigger = settings.DrawOnScreenShortcut },
            new HotkeyEntry { Label = "Start / stop recording", Trigger = settings.StartStopRecordingShortcut },
        };
    }
}
