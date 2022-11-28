using System;

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
}
