/////////////////////////////////
// THIS FILE IS AUTO-GENERATED //
/////////////////////////////////

namespace Clowd.Localization;

public enum StringsKeys
{
    SettingsGeneral_AddToExplorer,
    SettingsGeneral_AskToClose,
    SettingsGeneral_BehaviorDesc,
    SettingsGeneral_BehaviorTitle,
    SettingsGeneral_ColorScheme,
    SettingsGeneral_ExperimentalUpdates,
    SettingsGeneral_Language,
    SettingsGeneral_LanguageHelp,
    SettingsGeneral_StartWithWindows,
    SettingsGeneral_ThemeDesc,
    SettingsGeneral_ThemeTitle,
    SettingsGeneral_TransparencyEffects,
    SettingsHotkey_CaptureMonitor,
    SettingsHotkey_CaptureRegion,
    SettingsHotkey_CaptureWindow,
    SettingsHotkey_DrawOnScreen,
    SettingsHotkey_InvalidGesture,
    SettingsHotkey_NotRegistered,
    SettingsHotkey_Registered,
    SettingsHotkey_StartStopRec,
    SettingsHotkey_Unset,
    SettingsHotkey_UploadClipboard,
    SettingsHotkey_UploadFile,
    SettingsNav_About,
    SettingsNav_Capture,
    SettingsNav_Editor,
    SettingsNav_General,
    SettingsNav_Home,
    SettingsNav_Hotkeys,
    SettingsNav_RecentSessions,
    SettingsNav_RecentUploads,
    SettingsNav_SettingsHeader,
    SettingsNav_Uploads,
    SettingsNav_Video,
    SettingsUpdate_CheckingForUpdates,
    SettingsUpdate_CheckUpdatesBtn,
    SettingsUpdate_NoAvailableUpdates,
    SettingsUpdate_RestartBtn,
    SettingsUpdate_UpdateAvailableDesc,
    SettingsUpdate_UpdateAvailableTitle,
    SettingsUpdate_UpToDate,
    SettingsUpdate_Working,
}

public enum StringsPluralKeys
{
    HazCookies,
}

public enum StringsEnumKeys
{
    ColorScheme,
}

public enum ColorScheme
{
    System = 0,
    Dark = 1,
    Light = 2,
}

partial class Strings
{
    public static string SettingsGeneral_AddToExplorer => GetString(nameof(SettingsGeneral_AddToExplorer));
    public static string SettingsGeneral_AskToClose => GetString(nameof(SettingsGeneral_AskToClose));
    public static string SettingsGeneral_BehaviorDesc => GetString(nameof(SettingsGeneral_BehaviorDesc));
    public static string SettingsGeneral_BehaviorTitle => GetString(nameof(SettingsGeneral_BehaviorTitle));
    public static string SettingsGeneral_ColorScheme => GetString(nameof(SettingsGeneral_ColorScheme));
    public static string SettingsGeneral_ExperimentalUpdates => GetString(nameof(SettingsGeneral_ExperimentalUpdates));
    public static string SettingsGeneral_Language => GetString(nameof(SettingsGeneral_Language));
    public static string SettingsGeneral_LanguageHelp => GetString(nameof(SettingsGeneral_LanguageHelp));
    public static string SettingsGeneral_StartWithWindows => GetString(nameof(SettingsGeneral_StartWithWindows));
    public static string SettingsGeneral_ThemeDesc => GetString(nameof(SettingsGeneral_ThemeDesc));
    public static string SettingsGeneral_ThemeTitle => GetString(nameof(SettingsGeneral_ThemeTitle));
    public static string SettingsGeneral_TransparencyEffects => GetString(nameof(SettingsGeneral_TransparencyEffects));
    public static string SettingsHotkey_CaptureMonitor => GetString(nameof(SettingsHotkey_CaptureMonitor));
    public static string SettingsHotkey_CaptureRegion => GetString(nameof(SettingsHotkey_CaptureRegion));
    public static string SettingsHotkey_CaptureWindow => GetString(nameof(SettingsHotkey_CaptureWindow));
    public static string SettingsHotkey_DrawOnScreen => GetString(nameof(SettingsHotkey_DrawOnScreen));
    public static string SettingsHotkey_InvalidGesture => GetString(nameof(SettingsHotkey_InvalidGesture));
    public static string SettingsHotkey_NotRegistered => GetString(nameof(SettingsHotkey_NotRegistered));
    public static string SettingsHotkey_Registered => GetString(nameof(SettingsHotkey_Registered));
    public static string SettingsHotkey_StartStopRec => GetString(nameof(SettingsHotkey_StartStopRec));
    public static string SettingsHotkey_Unset => GetString(nameof(SettingsHotkey_Unset));
    public static string SettingsHotkey_UploadClipboard => GetString(nameof(SettingsHotkey_UploadClipboard));
    public static string SettingsHotkey_UploadFile => GetString(nameof(SettingsHotkey_UploadFile));
    public static string SettingsNav_About => GetString(nameof(SettingsNav_About));
    public static string SettingsNav_Capture => GetString(nameof(SettingsNav_Capture));
    public static string SettingsNav_Editor => GetString(nameof(SettingsNav_Editor));
    public static string SettingsNav_General => GetString(nameof(SettingsNav_General));
    public static string SettingsNav_Home => GetString(nameof(SettingsNav_Home));
    public static string SettingsNav_Hotkeys => GetString(nameof(SettingsNav_Hotkeys));
    public static string SettingsNav_RecentSessions => GetString(nameof(SettingsNav_RecentSessions));
    public static string SettingsNav_RecentUploads => GetString(nameof(SettingsNav_RecentUploads));
    public static string SettingsNav_SettingsHeader => GetString(nameof(SettingsNav_SettingsHeader));
    public static string SettingsNav_Uploads => GetString(nameof(SettingsNav_Uploads));
    public static string SettingsNav_Video => GetString(nameof(SettingsNav_Video));
    public static string SettingsUpdate_CheckingForUpdates => GetString(nameof(SettingsUpdate_CheckingForUpdates));
    public static string SettingsUpdate_CheckUpdatesBtn => GetString(nameof(SettingsUpdate_CheckUpdatesBtn));
    public static string SettingsUpdate_NoAvailableUpdates => GetString(nameof(SettingsUpdate_NoAvailableUpdates));
    public static string SettingsUpdate_RestartBtn => GetString(nameof(SettingsUpdate_RestartBtn));
    public static string SettingsUpdate_UpdateAvailableDesc(object A0) => String.Format(GetString(nameof(SettingsUpdate_UpdateAvailableDesc)), A0);
    public static string SettingsUpdate_UpdateAvailableTitle => GetString(nameof(SettingsUpdate_UpdateAvailableTitle));
    public static string SettingsUpdate_UpToDate => GetString(nameof(SettingsUpdate_UpToDate));
    public static string SettingsUpdate_Working(object A0) => String.Format(GetString(nameof(SettingsUpdate_Working)), A0);
    public static string HazCookies(double PV) => GetPlural(nameof(HazCookies), PV);
    public static ColorScheme[] ColorSchemeEnumValues => new ColorScheme[] { ColorScheme.System, ColorScheme.Dark, ColorScheme.Light };
    public static System.Collections.IEnumerable GetEnumValues(StringsEnumKeys resourceKey) => GetEnumValues(resourceKey.ToString());
    public static System.Collections.IEnumerable GetEnumValues(string resourceKey)
    {
        return resourceKey switch
        {
            "ColorScheme" => ColorSchemeEnumValues,
            _ => new object[0],
        };
    }
    public static string GetEnum(StringsEnumKeys resourceKey, int value) => GetEnum(resourceKey.ToString(), value);
    public static string GetEnum(string resourceKey, int value)
    {
        string keyName = resourceKey switch
        {
            "ColorScheme" => value switch
            {
                0 => "ColorScheme_E0_System",
                1 => "ColorScheme_E1_Dark",
                2 => "ColorScheme_E2_Light",
                _ => null
            },
            _ => null,
        };
        if (keyName is null) return "";
        return GetString(keyName) ?? "";
    }
}
