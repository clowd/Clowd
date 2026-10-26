#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Prisc.Legacy;

/// <summary>
/// Everything that still has to know the app's former name, Clowd: the persisted names an
/// install of Clowd left behind, and the platform-neutral migrations that carry a user's state
/// over to Prisc. This is the only file (with <c>LegacyClowdShellCleanup</c> and its tests) where
/// the old literals may appear — every other file refers to them through these constants.
///
/// Every migration takes its paths as parameters (so tests run against temp directories), is
/// idempotent, and never throws: failures are logged and the app starts as if there were nothing
/// to migrate. Nothing here ever modifies or deletes the legacy settings file, the legacy data
/// root or its <c>logs</c> — on Windows <c>%LOCALAPPDATA%\Clowd</c> is also the Velopack root of
/// an upgraded install.
/// </summary>
public static class LegacyClowd
{
    /// <summary>The folder name under ApplicationData (settings) and LocalAppData / the XDG data
    /// dir (sessions, previews, logs), and the Velopack root of a Windows install.</summary>
    public const string FolderName = "Clowd";

    public const string SettingsFileName = "Clowd.Settings.json";
    public const string DebugSettingsFileName = "Clowd.DEBUG.Settings.json";

    /// <summary>The <c>Editor</c> settings key that became <see cref="CurrentRestoreSessionsKey"/>.</summary>
    public const string RestoreSessionsKey = "RestoreSessionsOnClowdStart";
    public const string CurrentRestoreSessionsKey = "RestoreSessionsOnPriscStart";

    /// <summary>The built-in clwd.app provider's former type name: a key in
    /// <c>Uploads.ProviderConfig</c>, in exported CLWDUP strings, and the
    /// <c>UploadRecord.Provider</c> of uploads made before the rename.</summary>
    public const string UploadProviderTypeName = "ClowdUploadProvider";

    /// <summary>The provider's current type name. Spelled out because Prisc.Shared cannot
    /// reference Prisc.Upload.</summary>
    public const string CurrentUploadProviderTypeName = "PriscUploadProvider";

    /// <summary>The legacy main executable without its extension (also its process name and the
    /// Mach-O name inside the macOS bundle).</summary>
    public const string MainProcessName = "Clowd.Ui";
    public const string MainExeFileName = "Clowd.Ui.exe";

    public const string TelemetryOptOutVariable = "CLOWD_DISABLE_TELEMETRY";

    /// <summary>Owner names of the legacy share-region helper's windows, as window-capture
    /// sidecars recorded by Clowd report them (macOS name, Windows file name).</summary>
    public static readonly IReadOnlyList<string> ShareRegionProcessNames = ["clowd_share_region", "clowd_share_region.exe"];

    /// <summary>The HKCU Run value name and the Explorer verb key name (release / debug build).</summary>
    public const string RegistryName = "Clowd", RegistryNameDebug = "Clowd.DEBUG";

    public const string ShellExtensionPackageName = "Clowd.ShellExtension";
    public const string ShellExtensionDllPattern = "ClowdShellExt*.dll";

    /// <summary>The macOS LaunchAgent label (the plist is <c>~/Library/LaunchAgents/&lt;label&gt;.plist</c>).</summary>
    public const string LaunchAgentLabel = "com.clowd.Clowd", LaunchAgentLabelDebug = "com.clowd.Clowd.DEBUG";

    /// <summary>The legacy macOS bundle; its executable is <c>Contents/MacOS/</c> + <see cref="MainProcessName"/>.</summary>
    public const string MacBundleName = "Clowd.app";

    private const string SessionsFolderName = "sessions";
    private const string PreviewsFolderName = "previews";

    /// <summary>Marker in the new data root while a session path rewrite is outstanding; see
    /// <see cref="MigrateDataFolder(string, string, bool, Func{bool})"/>.</summary>
    public const string RewritePendingFileName = ".legacy-sessions-rewrite-pending";

    private static readonly JsonDocumentOptions _readOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ------------------------------------------------------------------------------------------
    // Upload providers
    // ------------------------------------------------------------------------------------------

    /// <summary>Maps the legacy provider type name to the current one; any other name (and null)
    /// is returned unchanged.</summary>
    public static string? MapProviderTypeName(string? typeName) =>
        String.Equals(typeName, UploadProviderTypeName, StringComparison.OrdinalIgnoreCase)
            ? CurrentUploadProviderTypeName
            : typeName;

    /// <summary>Renames a legacy provider key to the current type name. When the current key is
    /// already present it wins and the legacy entry is dropped. Null-safe; never throws.</summary>
    public static void NormalizeProviderKeys<T>(IDictionary<string, T>? providers)
    {
        if (providers == null)
            return;

        try
        {
            var legacyKeys = providers.Keys
                                      .Where(k => String.Equals(k, UploadProviderTypeName, StringComparison.OrdinalIgnoreCase))
                                      .ToList();

            foreach (var key in legacyKeys)
            {
                var value = providers[key];
                providers.Remove(key);

                if (!providers.ContainsKey(CurrentUploadProviderTypeName))
                    providers[CurrentUploadProviderTypeName] = value;
            }
        }
        catch (Exception ex)
        {
            Log("could not normalize upload provider keys: " + ex);
        }
    }

    /// <summary>Copies the built-in provider's entry under its legacy key as well, so an exported
    /// CLWDUP string still imports into a Clowd that looks providers up by the old type name. New
    /// clients drop the alias again on decode (<see cref="NormalizeProviderKeys{T}"/>, where the
    /// current key wins). Null-safe; never throws.</summary>
    public static void AddLegacyProviderAlias<T>(IDictionary<string, T>? providers)
    {
        try
        {
            if (providers != null && providers.TryGetValue(CurrentUploadProviderTypeName, out var value)
                && !providers.ContainsKey(UploadProviderTypeName))
                providers[UploadProviderTypeName] = value;
        }
        catch (Exception ex)
        {
            Log("could not add the legacy upload provider alias: " + ex);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Telemetry
    // ------------------------------------------------------------------------------------------

    /// <summary>Carries a user's <see cref="TelemetryOptOutVariable"/> over to
    /// <paramref name="newVariable"/> at process level (so child processes inherit it too). A
    /// value already set for the new variable is never overwritten. Never throws.</summary>
    public static void BridgeTelemetryOptOut(string newVariable)
    {
        try
        {
            if (String.IsNullOrEmpty(newVariable))
                return;

            if (Environment.GetEnvironmentVariable(newVariable) != null)
                return;

            var legacy = Environment.GetEnvironmentVariable(TelemetryOptOutVariable);
            if (String.IsNullOrEmpty(legacy))
                return;

            Environment.SetEnvironmentVariable(newVariable, legacy);
        }
        catch (Exception ex)
        {
            Log("could not bridge the telemetry opt-out: " + ex);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Copies the legacy settings file forward to <paramref name="newSettingsPath"/> when that
    /// file does not exist yet. The legacy file is <c>&lt;parent of the new folder&gt;/Clowd/</c>
    /// plus <see cref="SettingsFileName"/>, or <see cref="DebugSettingsFileName"/> when the new
    /// file name contains <c>.DEBUG.</c>. Keys renamed with the app are carried over
    /// (<see cref="RestoreSessionsKey"/>, <see cref="UploadProviderTypeName"/>); a file that does
    /// not parse is copied byte for byte so the normal load error path handles it. The legacy file
    /// is never modified. Returns true only when a file was written. Never throws.
    /// </summary>
    public static bool MigrateSettingsFile(string newSettingsPath)
    {
        try
        {
            if (String.IsNullOrEmpty(newSettingsPath) || File.Exists(newSettingsPath))
                return false;

            var fullNewPath = Path.GetFullPath(newSettingsPath);
            var newDirectory = Path.GetDirectoryName(fullNewPath);
            var parent = newDirectory == null ? null : Path.GetDirectoryName(newDirectory);
            if (String.IsNullOrEmpty(newDirectory) || String.IsNullOrEmpty(parent))
                return false;

            var debug = Path.GetFileName(fullNewPath).Contains(".DEBUG.", StringComparison.OrdinalIgnoreCase);
            var legacyPath = Path.Combine(parent, FolderName, debug ? DebugSettingsFileName : SettingsFileName);

            if (PathsEqual(legacyPath, fullNewPath) || !File.Exists(legacyPath))
                return false;

            var original = File.ReadAllBytes(legacyPath);
            var output = RenameLegacySettingsKeys(original) ?? original;

            Directory.CreateDirectory(newDirectory);

            var tempPath = fullNewPath + ".tmp";
            File.WriteAllBytes(tempPath, output);
            try
            {
                File.Move(tempPath, fullNewPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(fullNewPath))
            {
                // someone wrote the new file between our check and the move: theirs wins.
                TryDeleteFile(tempPath);
                return false;
            }

            Log("copied settings forward from " + legacyPath + " to " + fullNewPath);
            return true;
        }
        catch (Exception ex)
        {
            Log("could not copy the settings forward: " + ex);
            return false;
        }
    }

    /// <summary>The settings JSON with the renamed keys carried over, or null when it does not
    /// parse or has nothing to rename (the caller then copies the original bytes).</summary>
    private static byte[]? RenameLegacySettingsKeys(byte[] utf8)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(StripUtf8Bom(utf8), documentOptions: _readOptions) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }

        if (root == null)
            return null;

        var changed = false;

        if (GetPropertyIgnoreCase(root, "Editor") is JsonObject editor)
            changed |= RenameKey(editor, RestoreSessionsKey, CurrentRestoreSessionsKey);

        if (GetPropertyIgnoreCase(root, "Uploads") is JsonObject uploads
            && GetPropertyIgnoreCase(uploads, "ProviderConfig") is JsonObject providers)
            changed |= RenameKey(providers, UploadProviderTypeName, CurrentUploadProviderTypeName);

        if (!changed)
            return null;

        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return Encoding.UTF8.GetBytes(json);
    }

    private static ReadOnlySpan<byte> StripUtf8Bom(byte[] utf8) =>
        utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF
            ? utf8.AsSpan(3)
            : utf8.AsSpan();

    private static JsonNode? GetPropertyIgnoreCase(JsonObject obj, string name)
    {
        foreach (var property in obj)
        {
            if (String.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    /// <summary>Renames <paramref name="oldKey"/> (case-insensitive) to <paramref name="newKey"/>;
    /// when the new key is already present it is kept and the old one dropped.</summary>
    private static bool RenameKey(JsonObject obj, string oldKey, string newKey)
    {
        var oldNames = obj.Select(p => p.Key)
                          .Where(k => String.Equals(k, oldKey, StringComparison.OrdinalIgnoreCase))
                          .ToList();
        if (oldNames.Count == 0)
            return false;

        var hasNew = obj.Any(p => String.Equals(p.Key, newKey, StringComparison.OrdinalIgnoreCase));

        foreach (var oldName in oldNames)
        {
            var value = obj[oldName];
            obj.Remove(oldName);

            if (!hasNew)
            {
                obj[newKey] = value;
                hasNew = true;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------------------------------
    // Data folder (sessions)
    // ------------------------------------------------------------------------------------------

    /// <summary>True when a process of the legacy main executable is running. A failure to
    /// enumerate processes counts as running, so the migration that asks waits for a later start.</summary>
    public static bool IsLegacyProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(MainProcessName);
            foreach (var p in processes)
                p.Dispose();
            return processes.Length > 0;
        }
        catch (Exception ex)
        {
            Log("could not enumerate processes: " + ex);
            return true;
        }
    }

    /// <summary>
    /// True when a not-upgraded Clowd install exists beside us. Pure over its inputs:
    /// on Windows <c>&lt;localAppData&gt;/Clowd/current/Clowd.Ui.exe</c> exists and
    /// <c>&lt;localAppData&gt;/Clowd</c> is not <paramref name="ownInstallRoot"/>; on macOS any
    /// <c>&lt;applicationsDir&gt;/Clowd.app/Contents/MacOS/Clowd.Ui</c> exists and that bundle is
    /// not <paramref name="ownInstallRoot"/>. An upgraded install never matches: Velopack replaced
    /// <c>current\</c> (Windows) or the bundle contents (macOS), so the legacy executable is gone.
    /// Callers pass the real LocalAppData, <c>["/Applications", "~/Applications"]</c> (macOS)
    /// and the Velopack root (null for a dev build). Errs on the side of true; never throws.
    /// </summary>
    public static bool HasSeparateLegacyInstall(string? localAppData, IEnumerable<string>? applicationsDirs, string? ownInstallRoot)
    {
        try
        {
            if (!String.IsNullOrEmpty(localAppData))
            {
                var root = Path.Combine(localAppData, FolderName);
                if (File.Exists(Path.Combine(root, "current", MainExeFileName)) && !PathsEqual(root, ownInstallRoot))
                    return true;
            }

            foreach (var applications in applicationsDirs ?? Array.Empty<string>())
            {
                if (String.IsNullOrEmpty(applications))
                    continue;

                var bundle = Path.Combine(applications, MacBundleName);
                if (File.Exists(Path.Combine(bundle, "Contents", "MacOS", MainProcessName)) && !PathsEqual(bundle, ownInstallRoot))
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log("could not check for a separate legacy install: " + ex);
            return true;
        }
    }

    /// <summary>
    /// Moves the legacy sessions (<c>&lt;dataDirectory&gt;/Clowd/sessions/*</c>) into
    /// <c>&lt;newRoot&gt;/sessions</c> and rewrites the absolute paths inside their JSON; see
    /// <see cref="MigrateDataFolder(string, string, bool, Func{bool})"/>.
    /// </summary>
    public static void MigrateDataFolder(string dataDirectory, string newRoot, bool separateLegacyInstall) =>
        MigrateDataFolder(dataDirectory, newRoot, separateLegacyInstall, IsLegacyProcessRunning);

    /// <summary>
    /// Moves the legacy sessions into <c>&lt;newRoot&gt;/sessions</c>. Does nothing when
    /// <paramref name="separateLegacyInstall"/> is set, when the legacy sessions folder is missing
    /// or empty (unless a path rewrite is still pending, see below), or when
    /// <paramref name="isLegacyProcessRunning"/> (asked last, only when there is something to move)
    /// reports a running Clowd. Each session folder (and loose file) moves with a same-volume
    /// rename; one whose target exists, or that is locked, is left for a later start. Then every
    /// <c>*.json</c> under the new sessions folder that still names the legacy sessions folder has
    /// that prefix rewritten (this also fixes references between sessions), the legacy sessions
    /// folder is removed if empty, and the legacy previews cache is deleted. The legacy root and
    /// its <c>logs</c> are never touched. Idempotent; never throws.
    ///
    /// A marker (<see cref="RewritePendingFileName"/> in <paramref name="newRoot"/>, outside the
    /// sessions folder) is written before the first move and removed only once a rewrite pass
    /// finished with no failures. A rewrite that failed (a file held open by another process) or
    /// never ran (the process died between moving and rewriting) is therefore retried on every
    /// later start, even after the legacy sessions folder is gone.
    /// </summary>
    public static void MigrateDataFolder(string dataDirectory, string newRoot, bool separateLegacyInstall, Func<bool> isLegacyProcessRunning)
    {
        try
        {
            if (String.IsNullOrEmpty(dataDirectory) || String.IsNullOrEmpty(newRoot))
                return;

            if (separateLegacyInstall)
            {
                Log("a separate Clowd install is present; its data is left alone");
                return;
            }

            var legacyRoot = NormalizePath(Path.Combine(dataDirectory, FolderName));
            var fullNewRoot = NormalizePath(newRoot);
            if (PathsEqual(legacyRoot, fullNewRoot))
                return;

            var legacySessions = Path.Combine(legacyRoot, SessionsFolderName);
            var newSessions = Path.Combine(fullNewRoot, SessionsFolderName);
            var pending = Path.Combine(fullNewRoot, RewritePendingFileName);

            if (!Directory.Exists(legacySessions) || !Directory.EnumerateFileSystemEntries(legacySessions).Any())
            {
                // nothing left to move, but an earlier run's rewrite may not have finished. This
                // touches only the new folder, so it needs no running-Clowd check.
                if (File.Exists(pending) && Directory.Exists(newSessions) && RewriteSessionPaths(newSessions, legacySessions, newSessions) == 0)
                {
                    TryDeleteFile(pending);
                    Log("finished the pending session path rewrite in " + newSessions);
                }

                return;
            }

            if (isLegacyProcessRunning != null && isLegacyProcessRunning())
            {
                Log("Clowd is running; the session migration waits for a later start");
                return;
            }

            Directory.CreateDirectory(newSessions);

            // before the first move, so a run that dies between moving and rewriting is finished
            // by the next start
            var markerWritten = false;
            try
            {
                File.WriteAllText(pending, "");
                markerWritten = true;
            }
            catch (Exception ex)
            {
                Log("could not write " + pending + ": " + ex.Message);
            }

            var moved = 0;

            foreach (var source in Directory.GetDirectories(legacySessions))
            {
                var target = Path.Combine(newSessions, Path.GetFileName(source));
                if (Directory.Exists(target) || File.Exists(target))
                {
                    Log("not migrating " + source + ": " + target + " already exists");
                    continue;
                }

                try
                {
                    Directory.Move(source, target);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log("could not migrate " + source + " (retried next start): " + ex.Message);
                }
            }

            foreach (var source in Directory.GetFiles(legacySessions))
            {
                var target = Path.Combine(newSessions, Path.GetFileName(source));
                if (Directory.Exists(target) || File.Exists(target))
                {
                    Log("not migrating " + source + ": " + target + " already exists");
                    continue;
                }

                try
                {
                    File.Move(source, target, overwrite: false);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log("could not migrate " + source + " (retried next start): " + ex.Message);
                }
            }

            // one pass over the new sessions folder: the sessions moved just now, plus any moved
            // by an earlier run that still point at a session which only moved in this one.
            var failures = RewriteSessionPaths(newSessions, legacySessions, newSessions);
            if (failures == 0)
                TryDeleteFile(pending);
            else
                Log(failures + " session file(s) still name " + legacySessions + "; the rewrite is retried next start");

            try
            {
                // an empty legacy folder normally ends the migration for good, so it may only go
                // once nothing is left to rewrite, or while the marker carries the retry.
                if ((failures == 0 || markerWritten) && !Directory.EnumerateFileSystemEntries(legacySessions).Any())
                    Directory.Delete(legacySessions);
            }
            catch (Exception ex)
            {
                Log("could not remove " + legacySessions + ": " + ex.Message);
            }

            if (moved > 0)
            {
                // a cache of the old sessions' thumbnails; the new root grows its own.
                var legacyPreviews = Path.Combine(legacyRoot, PreviewsFolderName);
                try
                {
                    if (Directory.Exists(legacyPreviews))
                        Directory.Delete(legacyPreviews, recursive: true);
                }
                catch (Exception ex)
                {
                    Log("could not delete " + legacyPreviews + ": " + ex.Message);
                }
            }

            Log("migrated " + moved + " session entries from " + legacySessions + " to " + newSessions);
        }
        catch (Exception ex)
        {
            Log("session migration failed: " + ex);
        }
    }

    /// <summary>Rewrites, in every <c>*.json</c> under <paramref name="scanRoot"/> (recursively;
    /// <c>*.jsonl</c> and unparsable files are skipped), each string value that is
    /// <paramref name="legacySessions"/> or starts with it plus a separator. Returns the number of
    /// files that needed a rewrite and did not get one (a failed listing counts as one).</summary>
    private static int RewriteSessionPaths(string scanRoot, string legacySessions, string newSessions)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(scanRoot, "*", SearchOption.AllDirectories)
                             .Where(f => String.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
                             .ToList();
        }
        catch (Exception ex)
        {
            Log("could not list " + scanRoot + ": " + ex.Message);
            return 1;
        }

        var failures = 0;
        foreach (var file in files)
        {
            if (!RewriteJsonFile(file, legacySessions, newSessions))
                failures++;
        }

        return failures;
    }

    /// <summary>False only when the file needed a rewrite (or could not be read to tell) and did
    /// not get one; an unchanged or unparsable file counts as done.</summary>
    private static bool RewriteJsonFile(string file, string legacySessions, string newSessions)
    {
        try
        {
            var text = File.ReadAllText(file);

            // every path this could rewrite spells the legacy folder name out literally (JSON
            // never escapes ASCII letters), so most files are never parsed at all.
            if (text.IndexOf(FolderName, StringComparison.OrdinalIgnoreCase) < 0)
                return true;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text, documentOptions: _readOptions);
            }
            catch (Exception)
            {
                return true;
            }

            string? Map(string value) => RewritePathPrefix(value, legacySessions, newSessions);

            bool changed;
            if (root is JsonValue rootValue && rootValue.GetValueKind() == JsonValueKind.String)
            {
                var replaced = Map(rootValue.GetValue<string>());
                changed = replaced != null;
                if (changed)
                    root = JsonValue.Create(replaced);
            }
            else
            {
                changed = RewriteStrings(root, Map);
            }

            if (!changed || root == null)
                return true;

            var json = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = text.IndexOf('\n') >= 0,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            // a read-only file (restored from a backup or an archive) refuses the replace below,
            // and would refuse the app's own saves too, so the flag is cleared for good.
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);

            var tempPath = file + "." + Path.GetRandomFileName() + ".tmp";
            File.WriteAllText(tempPath, json);
            try
            {
                File.Move(tempPath, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the atomic replace is denied while another process (an indexer, an AV scan)
                // holds the file open without delete sharing; writing in place may still work.
                File.WriteAllText(file, json);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log("could not rewrite paths in " + file + ": " + ex.Message);
            return false;
        }
    }

    private static bool RewriteStrings(JsonNode? node, Func<string, string?> map)
    {
        var changed = false;

        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        var replaced = map(value.GetValue<string>());
                        if (replaced != null)
                        {
                            obj[key] = JsonValue.Create(replaced);
                            changed = true;
                        }
                    }
                    else
                    {
                        changed |= RewriteStrings(child, map);
                    }
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (child is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        var replaced = map(value.GetValue<string>());
                        if (replaced != null)
                        {
                            array[i] = JsonValue.Create(replaced);
                            changed = true;
                        }
                    }
                    else
                    {
                        changed |= RewriteStrings(child, map);
                    }
                }
                break;
        }

        return changed;
    }

    /// <summary>
    /// <paramref name="value"/> with a leading <paramref name="legacyPrefix"/> replaced by
    /// <paramref name="newPrefix"/>, or null when it does not start with it. The prefix has to be
    /// the whole value or be followed by a separator; <c>\</c> and <c>/</c> match each other, and
    /// the replacement is written with the separator the value used. Case-insensitive on Windows.
    /// </summary>
    public static string? RewritePathPrefix(string value, string legacyPrefix, string newPrefix)
    {
        if (String.IsNullOrEmpty(value) || String.IsNullOrEmpty(legacyPrefix))
            return null;

        legacyPrefix = legacyPrefix.TrimEnd('\\', '/');
        newPrefix = newPrefix.TrimEnd('\\', '/');

        var length = legacyPrefix.Length;
        if (length == 0 || value.Length < length)
            return null;

        var ignoreCase = OperatingSystem.IsWindows();
        char? separator = null;

        for (var i = 0; i < length; i++)
        {
            var a = value[i];
            var b = legacyPrefix[i];
            var aIsSeparator = a is '\\' or '/';
            var bIsSeparator = b is '\\' or '/';

            if (aIsSeparator || bIsSeparator)
            {
                if (!(aIsSeparator && bIsSeparator))
                    return null;
                separator ??= a;
                continue;
            }

            if (a != b && !(ignoreCase && Char.ToUpperInvariant(a) == Char.ToUpperInvariant(b)))
                return null;
        }

        if (value.Length > length && value[length] is not ('\\' or '/'))
            return null;

        var prefix = separator is char s ? newPrefix.Replace('\\', s).Replace('/', s) : newPrefix;
        return prefix + value.Substring(length);
    }

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string? a, string? b)
    {
        if (String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b))
            return false;

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try
        {
            return String.Equals(NormalizePath(a), NormalizePath(b), comparison);
        }
        catch (Exception)
        {
            return String.Equals(a, b, comparison);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // best effort: a leftover temp file is harmless
        }
    }

    private static void Log(string message) => Debug.WriteLine("LegacyClowd: " + message);
}
