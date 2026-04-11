using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Clowd.Ui.Models.Settings.Converters;

namespace Clowd.Ui.Models.Settings;

public sealed class SettingsRoot : IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    public SettingsGeneral General { get; set; } = new();
    public SettingsHotkey Hotkeys { get; set; } = new();
    public SettingsEditor Editor { get; set; } = new();
    public SettingsUpload Uploads { get; set; } = new();

    [JsonIgnore]
    private Timer? _saveTimer;
    [JsonIgnore]
    private bool _disposed;

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd");

    public static string SettingsFilePath =>
        Path.Combine(SettingsDirectory, "settings.json");

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new ColorJsonConverter(), new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SettingsRoot Load()
    {
        SettingsRoot root;
        var fileExisted = File.Exists(SettingsFilePath);
        try
        {
            if (fileExisted)
            {
                var json = File.ReadAllText(SettingsFilePath);
                root = JsonSerializer.Deserialize<SettingsRoot>(json, JsonOptions) ?? new SettingsRoot();
            }
            else
            {
                root = new SettingsRoot();
            }
        }
        catch (Exception)
        {
            // corrupt file — start fresh, but don't overwrite the old one until the user changes something
            root = new SettingsRoot();
            fileExisted = true;
        }

        root.AfterLoad();

        if (!fileExisted)
        {
            // first run: write a default settings.json so users can find/edit it
            root.SaveNow();
        }

        return root;
    }

    private void AfterLoad()
    {
        General.OnLoaded();
        Hotkeys.OnLoaded();
        Editor.OnLoaded();
        Uploads.OnLoaded();

        General.PropertyChanged += OnAnyChanged;
        Hotkeys.PropertyChanged += OnAnyChanged;
        Editor.PropertyChanged += OnAnyChanged;
        Uploads.PropertyChanged += OnAnyChanged;
    }

    private void OnAnyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ScheduleSave();
    }

    public void ScheduleSave()
    {
        if (_disposed) return;
        if (_saveTimer == null)
        {
            _saveTimer = new Timer(_ => SaveNow(), null, SaveDebounce, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var tempPath = SettingsFilePath + ".tmp";
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(tempPath, json);
            if (File.Exists(SettingsFilePath))
                File.Delete(SettingsFilePath);
            File.Move(tempPath, SettingsFilePath);
        }
        catch
        {
            // swallow — settings save failures shouldn't crash the app
        }
    }

    public void FlushAndDispose()
    {
        if (_disposed) return;
        _saveTimer?.Dispose();
        _saveTimer = null;
        SaveNow();
        _disposed = true;
    }

    public void Dispose() => FlushAndDispose();
}
