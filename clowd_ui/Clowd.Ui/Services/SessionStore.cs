using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clowd.Ui.Models;
using Clowd.Ui.Models.Settings;
using Clowd.Ui.Models.Settings.Converters;

namespace Clowd.Ui.Services;

/// <summary>
/// File-system backing store for <see cref="EditorSession"/>s. Sessions live
/// in <c>%APPDATA%\Clowd\sessions\</c>, one JSON file per session.
/// </summary>
public sealed class SessionStore
{
    public static string SessionsDirectory =>
        Path.Combine(SettingsRoot.SettingsDirectory, "sessions");

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new ColorJsonConverter(), new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyList<EditorSession> ListSessions()
    {
        if (!Directory.Exists(SessionsDirectory))
            return Array.Empty<EditorSession>();

        var result = new List<EditorSession>();
        foreach (var file in Directory.EnumerateFiles(SessionsDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var session = JsonSerializer.Deserialize<EditorSession>(json, JsonOptions);
                if (session != null)
                    result.Add(session);
            }
            catch
            {
                // Skip corrupt session files.
            }
        }

        return result.OrderByDescending(s => s.ModifiedUtc).ToList();
    }

    public void Save(EditorSession session)
    {
        Directory.CreateDirectory(SessionsDirectory);
        session.ModifiedUtc = DateTime.UtcNow;
        var path = GetSessionPath(session.Id);
        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(session, JsonOptions);
        File.WriteAllText(tempPath, json);
        if (File.Exists(path))
            File.Delete(path);
        File.Move(tempPath, path);
    }

    public bool Delete(string id)
    {
        var path = GetSessionPath(id);
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public EditorSession? TryLoad(string id)
    {
        var path = GetSessionPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<EditorSession>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string GetSessionPath(string id)
        => Path.Combine(SessionsDirectory, id + ".json");
}
