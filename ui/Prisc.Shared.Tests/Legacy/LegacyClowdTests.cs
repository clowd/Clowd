using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Prisc.Config;
using Prisc.Legacy;
using Prisc.Upload;
using Xunit;

namespace Prisc.Shared.Tests
{
    /// <summary>The migrations from the app's former name (see <see cref="LegacyClowd"/>). Every
    /// test works in its own temp tree; nothing touches the registry or the real data folders.</summary>
    public class LegacyClowdTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PriscLegacyTests", Guid.NewGuid().ToString("N"));

        public LegacyClowdTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch { }
        }

        // ------------------------------------------------------------------------------------
        // Settings copy-forward
        // ------------------------------------------------------------------------------------

        private string AppData => Path.Combine(_root, "AppData");
        private string LegacySettings => Path.Combine(AppData, "Clowd", "Clowd.Settings.json");
        private string LegacyDebugSettings => Path.Combine(AppData, "Clowd", "Clowd.DEBUG.Settings.json");
        private string NewSettings => Path.Combine(AppData, "Prisc", "Prisc.Settings.json");
        private string NewDebugSettings => Path.Combine(AppData, "Prisc", "Prisc.DEBUG.Settings.json");

        private static void WriteFile(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
        }

        private const string LegacySettingsJson = """
            {
              // written by Clowd 4.11
              "Editor": {
                "RestoreSessionsOnClowdStart": false,
              },
              "Uploads": {
                "ProviderConfig": {
                  "ClowdUploadProvider": {
                    "IsEnabled": true,
                    "DefaultFor": "Text",
                    "Settings": { "ServerUrl": "https://paste.example.com" }
                  },
                  "ImgurUploadProvider": { "IsEnabled": true }
                }
              }
            }
            """;

        [Fact]
        public void Settings_AreCopiedForwardOnce_WithRenamedKeys_AndTheLegacyFileUntouched()
        {
            WriteFile(LegacySettings, LegacySettingsJson);
            var legacyBefore = File.ReadAllBytes(LegacySettings);

            Assert.True(LegacyClowd.MigrateSettingsFile(NewSettings));
            Assert.True(File.Exists(NewSettings));
            Assert.False(File.Exists(NewSettings + ".tmp"));

            var root = JsonNode.Parse(File.ReadAllText(NewSettings)).AsObject();
            var editor = root["Editor"].AsObject();
            Assert.False(editor.ContainsKey("RestoreSessionsOnClowdStart"));
            Assert.False(editor["RestoreSessionsOnPriscStart"].GetValue<bool>());

            var providers = root["Uploads"]["ProviderConfig"].AsObject();
            Assert.False(providers.ContainsKey("ClowdUploadProvider"));
            Assert.Equal("https://paste.example.com", providers["PriscUploadProvider"]["Settings"]["ServerUrl"].GetValue<string>());
            Assert.True(providers.ContainsKey("ImgurUploadProvider"));

            // the legacy file is never modified (a downgrade still finds it intact)
            Assert.Equal(legacyBefore, File.ReadAllBytes(LegacySettings));

            // a second call finds the new file and does nothing
            var newBefore = File.ReadAllBytes(NewSettings);
            var newWritten = File.GetLastWriteTimeUtc(NewSettings);
            Assert.False(LegacyClowd.MigrateSettingsFile(NewSettings));
            Assert.Equal(newBefore, File.ReadAllBytes(NewSettings));
            Assert.Equal(newWritten, File.GetLastWriteTimeUtc(NewSettings));
            Assert.Equal(legacyBefore, File.ReadAllBytes(LegacySettings));
        }

        [Fact]
        public void Settings_MigratedFile_LoadsWithTheUsersChoices()
        {
            WriteFile(LegacySettings, LegacySettingsJson);
            Assert.True(LegacyClowd.MigrateSettingsFile(NewSettings));

            var loaded = SettingsService.Load(NewSettings);

            // the compiled-in default is true, so false can only have come from the old key
            Assert.False(loaded.Editor.RestoreSessionsOnPriscStart);
            Assert.True(loaded.Uploads.ProviderConfig.ContainsKey("PriscUploadProvider"));
            Assert.False(loaded.Uploads.ProviderConfig.ContainsKey("ClowdUploadProvider"));
        }

        [Fact]
        public void Settings_DebugFileName_ReadsOnlyTheDebugLegacyFile()
        {
            WriteFile(LegacySettings, """{ "Editor": { "SnapToObjects": true } }""");
            WriteFile(LegacyDebugSettings, """{ "Editor": { "SnapToObjects": false } }""");

            Assert.True(LegacyClowd.MigrateSettingsFile(NewDebugSettings));
            Assert.Equal(File.ReadAllBytes(LegacyDebugSettings), File.ReadAllBytes(NewDebugSettings));
            Assert.False(File.Exists(NewSettings));

            Assert.True(LegacyClowd.MigrateSettingsFile(NewSettings));
            Assert.Equal(File.ReadAllBytes(LegacySettings), File.ReadAllBytes(NewSettings));
        }

        [Fact]
        public void Settings_DebugFileName_WithoutADebugLegacyFile_DoesNotFallBackToRelease()
        {
            WriteFile(LegacySettings, LegacySettingsJson);

            Assert.False(LegacyClowd.MigrateSettingsFile(NewDebugSettings));
            Assert.False(File.Exists(NewDebugSettings));
        }

        [Fact]
        public void Settings_WhenTheNewKeysAlreadyExist_TheyWinAndTheOldOnesAreDropped()
        {
            WriteFile(LegacySettings, """
                {
                  "Editor": { "RestoreSessionsOnClowdStart": false, "RestoreSessionsOnPriscStart": true },
                  "Uploads": { "ProviderConfig": {
                    "ClowdUploadProvider": { "IsEnabled": false },
                    "PriscUploadProvider": { "IsEnabled": true }
                  } }
                }
                """);

            Assert.True(LegacyClowd.MigrateSettingsFile(NewSettings));

            var root = JsonNode.Parse(File.ReadAllText(NewSettings)).AsObject();
            var editor = root["Editor"].AsObject();
            Assert.False(editor.ContainsKey("RestoreSessionsOnClowdStart"));
            Assert.True(editor["RestoreSessionsOnPriscStart"].GetValue<bool>());

            var providers = root["Uploads"]["ProviderConfig"].AsObject();
            Assert.False(providers.ContainsKey("ClowdUploadProvider"));
            Assert.True(providers["PriscUploadProvider"]["IsEnabled"].GetValue<bool>());
        }

        [Fact]
        public void Settings_KeyMatchingIsCaseInsensitive()
        {
            WriteFile(LegacySettings, """{ "editor": { "restoreSessionsOnClowdStart": false } }""");

            Assert.True(LegacyClowd.MigrateSettingsFile(NewSettings));

            var editor = JsonNode.Parse(File.ReadAllText(NewSettings))["editor"].AsObject();
            Assert.Single(editor);
            Assert.False(editor["RestoreSessionsOnPriscStart"].GetValue<bool>());
        }

        [Fact]
        public void Settings_MalformedJson_IsCopiedVerbatim()
        {
            WriteFile(LegacySettings, "{ \"Editor\": { \"RestoreSessionsOnClowdStart\": fal");

            Assert.True(LegacyClowd.MigrateSettingsFile(NewSettings));
            Assert.Equal(File.ReadAllBytes(LegacySettings), File.ReadAllBytes(NewSettings));
        }

        [Fact]
        public void Settings_NothingHappens_WhenTheNewFileExists()
        {
            WriteFile(LegacySettings, LegacySettingsJson);
            WriteFile(NewSettings, "{ \"Editor\": {} }");

            Assert.False(LegacyClowd.MigrateSettingsFile(NewSettings));
            Assert.Equal("{ \"Editor\": {} }", File.ReadAllText(NewSettings));
        }

        [Fact]
        public void Settings_NothingHappens_WithoutALegacyFile()
        {
            Assert.False(LegacyClowd.MigrateSettingsFile(NewSettings));
            Assert.False(File.Exists(NewSettings));
            Assert.False(Directory.Exists(Path.GetDirectoryName(NewSettings)));
        }

        // ------------------------------------------------------------------------------------
        // Upload providers
        // ------------------------------------------------------------------------------------

        [Fact]
        public void MapProviderTypeName_MapsOnlyTheLegacyName()
        {
            Assert.Equal("PriscUploadProvider", LegacyClowd.MapProviderTypeName("ClowdUploadProvider"));
            Assert.Equal("PriscUploadProvider", LegacyClowd.MapProviderTypeName("PriscUploadProvider"));
            Assert.Equal("ImgurUploadProvider", LegacyClowd.MapProviderTypeName("ImgurUploadProvider"));
            Assert.Null(LegacyClowd.MapProviderTypeName(null));
            Assert.Equal("", LegacyClowd.MapProviderTypeName(""));
            Assert.Equal(nameof(PriscUploadProvider), LegacyClowd.CurrentUploadProviderTypeName);
        }

        [Fact]
        public void NormalizeProviderKeys_RenamesTheLegacyKey()
        {
            var providers = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["ClowdUploadProvider"] = 1,
                ["ImgurUploadProvider"] = 2,
            };

            LegacyClowd.NormalizeProviderKeys(providers);

            Assert.Equal(new[] { "ImgurUploadProvider", "PriscUploadProvider" }, providers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(1, providers["PriscUploadProvider"]);
            Assert.Equal(2, providers["ImgurUploadProvider"]);
        }

        [Fact]
        public void NormalizeProviderKeys_OnCollision_KeepsTheCurrentKey()
        {
            var providers = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["ClowdUploadProvider"] = 1,
                ["PriscUploadProvider"] = 3,
            };

            LegacyClowd.NormalizeProviderKeys(providers);

            Assert.Single(providers);
            Assert.Equal(3, providers["PriscUploadProvider"]);

            // null and already-current dictionaries are fine
            LegacyClowd.NormalizeProviderKeys<int>(null);
            LegacyClowd.NormalizeProviderKeys(providers);
            Assert.Single(providers);
        }

        [Fact]
        public void LegacyExportString_ImportsIntoThePriscProvider()
        {
            _ = typeof(MimeProvider).Assembly;

            var payload = new UploadTransferPayload { App = "4.11.0", Exported = DateTimeOffset.UnixEpoch };
            payload.Providers[LegacyClowd.UploadProviderTypeName] = new UploadTransferEntry
            {
                Name = "Clowd",
                IsEnabled = true,
                DefaultFor = "Text",
                Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["ServerUrl"] = "https://paste.example.com" },
            };
            var exported = UploadSettingsTransfer.Encode(payload);

            Assert.True(UploadSettingsTransfer.TryDecode(exported, out var decoded));
            Assert.False(decoded.Providers.ContainsKey(LegacyClowd.UploadProviderTypeName));
            var entry = decoded.Providers[nameof(PriscUploadProvider)];

            var settings = new SettingsUpload();
            settings.DiscoverProviders();
            Assert.True(settings.ImportProvider(nameof(PriscUploadProvider), entry));

            var info = settings.Providers.Single(p => p.Provider is PriscUploadProvider);
            Assert.True(info.IsEnabled);
            Assert.True(info.DefaultFor.HasFlag(SupportedUploadType.Text));
            Assert.Equal("https://paste.example.com", ((PriscUploadProvider)info.Provider).ServerUrl);
            Assert.True(settings.ProviderConfig.ContainsKey(nameof(PriscUploadProvider)));
            Assert.False(settings.ProviderConfig.ContainsKey(LegacyClowd.UploadProviderTypeName));

            // the old type name still resolves (old UploadRecords, old import call sites)
            Assert.Same(info, settings.GetProviderByTypeName(LegacyClowd.UploadProviderTypeName));
        }

        [Fact]
        public void DiscoverProviders_AppliesConfigSavedUnderTheLegacyKey()
        {
            _ = typeof(MimeProvider).Assembly;

            var settings = new SettingsUpload();
            settings.ProviderConfig[LegacyClowd.UploadProviderTypeName] = new UploadProviderConfig
            {
                IsEnabled = false,
                Settings = new Dictionary<string, string> { ["ServerUrl"] = "https://paste.example.com" },
            };

            settings.DiscoverProviders();

            var info = settings.Providers.Single(p => p.Provider is PriscUploadProvider);
            Assert.False(info.IsEnabled);
            Assert.Equal("https://paste.example.com", ((PriscUploadProvider)info.Provider).ServerUrl);
            Assert.False(settings.ProviderConfig.ContainsKey(LegacyClowd.UploadProviderTypeName));
        }

        // ------------------------------------------------------------------------------------
        // Telemetry opt-out
        // ------------------------------------------------------------------------------------

        private static void WithLegacyOptOut(string value, Action<string> test)
        {
            var newVariable = "PRISC_LEGACY_TEST_" + Guid.NewGuid().ToString("N");
            var saved = Environment.GetEnvironmentVariable(LegacyClowd.TelemetryOptOutVariable);
            try
            {
                Environment.SetEnvironmentVariable(LegacyClowd.TelemetryOptOutVariable, value);
                test(newVariable);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LegacyClowd.TelemetryOptOutVariable, saved);
                Environment.SetEnvironmentVariable(newVariable, null);
            }
        }

        [Fact]
        public void TelemetryOptOut_IsCopiedToTheNewVariable()
        {
            WithLegacyOptOut("1", newVariable =>
            {
                LegacyClowd.BridgeTelemetryOptOut(newVariable);
                Assert.Equal("1", Environment.GetEnvironmentVariable(newVariable));
            });
        }

        [Fact]
        public void TelemetryOptOut_NeverOverwritesTheNewVariable()
        {
            WithLegacyOptOut("1", newVariable =>
            {
                Environment.SetEnvironmentVariable(newVariable, "keep");
                LegacyClowd.BridgeTelemetryOptOut(newVariable);
                Assert.Equal("keep", Environment.GetEnvironmentVariable(newVariable));
            });
        }

        [Fact]
        public void TelemetryOptOut_WithoutTheOldVariable_SetsNothing()
        {
            WithLegacyOptOut(null, newVariable =>
            {
                LegacyClowd.BridgeTelemetryOptOut(newVariable);
                Assert.Null(Environment.GetEnvironmentVariable(newVariable));
            });
        }

        // ------------------------------------------------------------------------------------
        // Separate legacy install
        // ------------------------------------------------------------------------------------

        private static void Touch(string path) => WriteFile(path, "");

        [Fact]
        public void SeparateInstall_WindowsLegacyExe_WithADifferentOwnRoot()
        {
            var localAppData = Path.Combine(_root, "Local");
            Touch(Path.Combine(localAppData, "Clowd", "current", "Clowd.Ui.exe"));

            Assert.True(LegacyClowd.HasSeparateLegacyInstall(localAppData, Array.Empty<string>(), Path.Combine(localAppData, "Prisc")));
            // a dev build has no install root at all
            Assert.True(LegacyClowd.HasSeparateLegacyInstall(localAppData, null, null));
        }

        [Fact]
        public void SeparateInstall_IsFalse_WhenTheLegacyRootIsOurOwn()
        {
            var localAppData = Path.Combine(_root, "Local");
            Touch(Path.Combine(localAppData, "Clowd", "current", "Clowd.Ui.exe"));

            Assert.False(LegacyClowd.HasSeparateLegacyInstall(localAppData, Array.Empty<string>(), Path.Combine(localAppData, "Clowd")));
            Assert.False(LegacyClowd.HasSeparateLegacyInstall(localAppData, Array.Empty<string>(),
                Path.Combine(localAppData, "Clowd") + Path.DirectorySeparatorChar));
        }

        [Fact]
        public void SeparateInstall_IsFalse_ForAnUpgradedWindowsTree()
        {
            var localAppData = Path.Combine(_root, "Local");
            Touch(Path.Combine(localAppData, "Clowd", "current", "Prisc.Ui.exe"));

            Assert.False(LegacyClowd.HasSeparateLegacyInstall(localAppData, Array.Empty<string>(), null));
        }

        [Fact]
        public void SeparateInstall_MacLegacyBundle()
        {
            var applications = Path.Combine(_root, "Applications");
            var bundle = Path.Combine(applications, "Clowd.app");
            Touch(Path.Combine(bundle, "Contents", "MacOS", "Clowd.Ui"));

            var dirs = new[] { Path.Combine(_root, "NoSuchApplications"), applications };
            Assert.True(LegacyClowd.HasSeparateLegacyInstall(null, dirs, null));
            Assert.True(LegacyClowd.HasSeparateLegacyInstall(null, dirs, Path.Combine(applications, "Prisc.app")));
            // the bundle we are running from is not a separate install
            Assert.False(LegacyClowd.HasSeparateLegacyInstall(null, dirs, bundle));
        }

        [Fact]
        public void SeparateInstall_IsFalse_ForAnUpgradedMacBundle()
        {
            var applications = Path.Combine(_root, "Applications");
            Touch(Path.Combine(applications, "Clowd.app", "Contents", "MacOS", "Prisc.Ui"));

            Assert.False(LegacyClowd.HasSeparateLegacyInstall(null, new[] { applications }, null));
        }

        [Fact]
        public void SeparateInstall_IsFalse_WhenNothingIsInstalled()
        {
            Assert.False(LegacyClowd.HasSeparateLegacyInstall(Path.Combine(_root, "Local"), new[] { Path.Combine(_root, "Applications") }, null));
            Assert.False(LegacyClowd.HasSeparateLegacyInstall(null, null, null));
        }

        // ------------------------------------------------------------------------------------
        // Data folder (sessions)
        // ------------------------------------------------------------------------------------

        private string DataDirectory => Path.Combine(_root, "data");
        private string LegacyRoot => Path.Combine(DataDirectory, "Clowd");
        private string LegacySessions => Path.Combine(LegacyRoot, "sessions");
        private string NewRoot => Path.Combine(DataDirectory, "Prisc");
        private string NewSessions => Path.Combine(NewRoot, "sessions");

        private void Migrate(bool separateLegacyInstall = false, bool legacyRunning = false) =>
            LegacyClowd.MigrateDataFolder(DataDirectory, NewRoot, separateLegacyInstall, () => legacyRunning);

        private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

        /// <summary>A legacy session folder with a session.json pointing into itself.</summary>
        private void CreateLegacySession(string name)
        {
            WriteFile(Path.Combine(LegacySessions, name, "session.json"),
                Json(new { Name = name, Path = Path.Combine(LegacySessions, name, "capture.png") }));
        }

        [Fact]
        public void Sessions_AreMoved_AndTheirAbsolutePathsRewritten()
        {
            var native = Path.Combine(LegacySessions, "A", "capture.png");
            var forward = native.Replace('\\', '/');
            var backslash = native.Replace('/', '\\');
            var otherSession = Path.Combine(LegacySessions, "B", "video.mp4");

            WriteFile(Path.Combine(LegacySessions, "A", "session.json"), Json(new
            {
                Name = "A",
                Number = 1.5,
                Root = LegacySessions,
                Media = new
                {
                    Path = native,
                    Items = new object[] { new { Src = forward }, new { Src = backslash }, 42, null },
                    Unrelated = Path.Combine(_root, "elsewhere", "Clowd", "sessions", "x.png"),
                    Sibling = LegacySessions + "X" + Path.DirectorySeparatorChar + "y.png",
                },
            }));
            WriteFile(Path.Combine(LegacySessions, "A", "edit", "videoedit.json"), Json(new { Clips = new[] { new { Source = otherSession } } }));
            var jsonl = "{\"Path\":" + JsonSerializer.Serialize(native) + "}\n";
            WriteFile(Path.Combine(LegacySessions, "A", "input.jsonl"), jsonl);
            CreateLegacySession("B");
            WriteFile(Path.Combine(LegacySessions, "loose.txt"), "loose");

            Migrate();

            Assert.False(Directory.Exists(LegacySessions));
            Assert.True(File.Exists(Path.Combine(NewSessions, "A", "session.json")));
            Assert.True(File.Exists(Path.Combine(NewSessions, "B", "session.json")));
            Assert.Equal("loose", File.ReadAllText(Path.Combine(NewSessions, "loose.txt")));

            var expected = Path.Combine(NewSessions, "A", "capture.png");
            var a = JsonNode.Parse(File.ReadAllText(Path.Combine(NewSessions, "A", "session.json")));
            Assert.Equal(NewSessions, a["Root"].GetValue<string>());
            Assert.Equal(expected, a["Media"]["Path"].GetValue<string>());
            Assert.Equal(expected.Replace('\\', '/'), a["Media"]["Items"][0]["Src"].GetValue<string>());
            Assert.Equal(expected.Replace('/', '\\'), a["Media"]["Items"][1]["Src"].GetValue<string>());
            Assert.Equal(42, a["Media"]["Items"][2].GetValue<int>());
            Assert.Null(a["Media"]["Items"][3]);
            Assert.Equal(Path.Combine(_root, "elsewhere", "Clowd", "sessions", "x.png"), a["Media"]["Unrelated"].GetValue<string>());
            Assert.Equal(LegacySessions + "X" + Path.DirectorySeparatorChar + "y.png", a["Media"]["Sibling"].GetValue<string>());
            Assert.Equal("1.5", a["Number"].ToJsonString());

            // nested files, and a reference from one session into another
            var edit = JsonNode.Parse(File.ReadAllText(Path.Combine(NewSessions, "A", "edit", "videoedit.json")));
            Assert.Equal(Path.Combine(NewSessions, "B", "video.mp4"), edit["Clips"][0]["Source"].GetValue<string>());

            var b = JsonNode.Parse(File.ReadAllText(Path.Combine(NewSessions, "B", "session.json")));
            Assert.Equal(Path.Combine(NewSessions, "B", "capture.png"), b["Path"].GetValue<string>());

            // *.jsonl is never rewritten
            Assert.Equal(jsonl, File.ReadAllText(Path.Combine(NewSessions, "A", "input.jsonl")));
        }

        [Fact]
        public void Sessions_AlreadyMigrated_HaveTheirReferencesToNewlyMovedSessionsRewritten()
        {
            // B moved on an earlier start; A was locked then and moves now
            WriteFile(Path.Combine(NewSessions, "B", "session.json"),
                Json(new { Ref = Path.Combine(LegacySessions, "A", "capture.png") }));
            CreateLegacySession("A");

            Migrate();

            var b = JsonNode.Parse(File.ReadAllText(Path.Combine(NewSessions, "B", "session.json")));
            Assert.Equal(Path.Combine(NewSessions, "A", "capture.png"), b["Ref"].GetValue<string>());
            Assert.True(Directory.Exists(Path.Combine(NewSessions, "A")));
        }

        [Fact]
        public void Sessions_Migration_DeletesPreviews_AndKeepsTheLegacyRootAndLogs()
        {
            CreateLegacySession("A");
            WriteFile(Path.Combine(LegacyRoot, "previews", "a.webp"), "x");
            WriteFile(Path.Combine(LegacyRoot, "logs", "app.log"), "log");
            WriteFile(Path.Combine(LegacyRoot, "Update.exe"), "stub");

            Migrate();

            Assert.False(Directory.Exists(Path.Combine(LegacyRoot, "previews")));
            Assert.True(Directory.Exists(LegacyRoot));
            Assert.Equal("log", File.ReadAllText(Path.Combine(LegacyRoot, "logs", "app.log")));
            Assert.Equal("stub", File.ReadAllText(Path.Combine(LegacyRoot, "Update.exe")));
            Assert.False(Directory.Exists(LegacySessions));
        }

        [Fact]
        public void Sessions_WithAnExistingTarget_AreLeftInPlace_AndASecondRunIsANoOp()
        {
            CreateLegacySession("A");
            CreateLegacySession("C");
            WriteFile(Path.Combine(NewSessions, "C", "session.json"), "{ \"Name\": \"new C\" }");
            WriteFile(Path.Combine(LegacyRoot, "previews", "a.webp"), "x");

            Migrate();

            Assert.True(File.Exists(Path.Combine(NewSessions, "A", "session.json")));
            Assert.True(File.Exists(Path.Combine(LegacySessions, "C", "session.json")));
            Assert.Equal("{ \"Name\": \"new C\" }", File.ReadAllText(Path.Combine(NewSessions, "C", "session.json")));

            var snapshot = SnapshotTree(DataDirectory);
            Migrate();
            Assert.Equal(snapshot, SnapshotTree(DataDirectory));
        }

        [Fact]
        public void Sessions_ALockedSession_IsSkipped_AndMovesOnALaterRun()
        {
            // a directory with an open file cannot be renamed on Windows; elsewhere it can
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory renames ignore open files outside Windows.");

            CreateLegacySession("A");
            CreateLegacySession("L");

            using (new FileStream(Path.Combine(LegacySessions, "L", "session.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Migrate();
            }

            Assert.True(Directory.Exists(Path.Combine(NewSessions, "A")));
            Assert.True(Directory.Exists(Path.Combine(LegacySessions, "L")));
            Assert.False(Directory.Exists(Path.Combine(NewSessions, "L")));

            Migrate();

            Assert.True(File.Exists(Path.Combine(NewSessions, "L", "session.json")));
            Assert.False(Directory.Exists(LegacySessions));
            var l = JsonNode.Parse(File.ReadAllText(Path.Combine(NewSessions, "L", "session.json")));
            Assert.Equal(Path.Combine(NewSessions, "L", "capture.png"), l["Path"].GetValue<string>());
        }

        [Fact]
        public void Sessions_FullyMigrated_ASecondRunIsANoOp()
        {
            CreateLegacySession("A");
            Migrate();

            var snapshot = SnapshotTree(DataDirectory);
            Migrate();
            Assert.Equal(snapshot, SnapshotTree(DataDirectory));
        }

        [Fact]
        public void Sessions_NothingMoves_WithASeparateLegacyInstall()
        {
            CreateLegacySession("A");
            WriteFile(Path.Combine(LegacyRoot, "previews", "a.webp"), "x");

            Migrate(separateLegacyInstall: true);

            Assert.True(Directory.Exists(Path.Combine(LegacySessions, "A")));
            Assert.True(Directory.Exists(Path.Combine(LegacyRoot, "previews")));
            Assert.False(Directory.Exists(NewRoot));
        }

        [Fact]
        public void Sessions_NothingMoves_WhileTheLegacyAppIsRunning()
        {
            CreateLegacySession("A");

            Migrate(legacyRunning: true);

            Assert.True(Directory.Exists(Path.Combine(LegacySessions, "A")));
            Assert.False(Directory.Exists(NewRoot));
        }

        [Fact]
        public void Sessions_NothingToMigrate_CreatesNothing()
        {
            Migrate();
            Assert.False(Directory.Exists(NewRoot));

            Directory.CreateDirectory(LegacySessions);
            Migrate();
            Assert.False(Directory.Exists(NewRoot));
            Assert.True(Directory.Exists(LegacySessions));
        }

        [Fact]
        public void RewritePathPrefix_MatchesWholeSegmentsOnly()
        {
            var legacy = Path.Combine(_root, "Clowd", "sessions");
            var current = Path.Combine(_root, "Prisc", "sessions");

            Assert.Equal(current, LegacyClowd.RewritePathPrefix(legacy, legacy, current));
            Assert.Equal(current + Path.DirectorySeparatorChar, LegacyClowd.RewritePathPrefix(legacy + Path.DirectorySeparatorChar, legacy, current));
            Assert.Null(LegacyClowd.RewritePathPrefix(legacy + "2", legacy, current));
            Assert.Null(LegacyClowd.RewritePathPrefix("relative/path.png", legacy, current));
            Assert.Null(LegacyClowd.RewritePathPrefix("", legacy, current));

            var upper = LegacyClowd.RewritePathPrefix(legacy.ToUpperInvariant() + Path.DirectorySeparatorChar + "a.png", legacy, current);
            if (OperatingSystem.IsWindows())
                Assert.Equal(Path.Combine(current, "a.png"), upper);
            else
                Assert.Null(upper);
        }

        private static string SnapshotTree(string root) =>
            String.Join("\n", Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                                       .OrderBy(p => p, StringComparer.Ordinal)
                                       .Select(p => Path.GetRelativePath(root, p) + "|"
                                                    + (File.Exists(p) ? Convert.ToBase64String(File.ReadAllBytes(p)) : "<dir>")));
    }
}
