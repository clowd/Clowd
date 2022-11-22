﻿using System.ComponentModel;

namespace Clowd.Config;

public class SettingsCapture : CategoryBase
{
    [DisplayName("Capture with cursor")]
    [Description("If this is enabled, the cursor will be shown in screenshots")]
    public bool ScreenshotWithCursor
    {
        get => _screenshotWithCursor;
        set => Set(ref _screenshotWithCursor, value);
    }

    [Browsable(false)]
    [Description("If this is true, the Capture window will try to detect and highlight different windows as you hover over them.")]
    public bool DetectWindows
    {
        get => _detectWindows;
        set => Set(ref _detectWindows, value);
    }

    public bool HideTipsPanel
    {
        get => _hideTipsPanel;
        set => Set(ref _hideTipsPanel, value);
    }

    public bool OpenSavedInExplorer
    {
        get => _openSavedInExplorer;
        set => Set(ref _openSavedInExplorer, value);
    }

    public string FilenamePattern
    {
        get => _filenamePattern;
        set => Set(ref _filenamePattern, value);
    }

    private string _filenamePattern = "yyyy-MM-dd HH-mm-ss";
    private bool _screenshotWithCursor = true;
    private bool _detectWindows = true;
    private bool _hideTipsPanel;
    private bool _openSavedInExplorer = true;
}
