using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Clowd.Config;
using Xunit;

namespace Clowd.Shared.Tests
{
    /// <summary>
    /// A settings file written by the (never-released, since removed) raster-v1 build may still
    /// exist locally. It carries the removed "RasterToolsEnabled" flag, stale "Brush"/"Eraser"
    /// names in the toolbar lists, and a Tools dictionary entry keyed by the removed "Brush" enum
    /// member. Loading it must degrade gracefully: unknown properties and unparseable dictionary
    /// keys are dropped, everything else binds, and the toolbar resolvers scrub the stale names.
    /// </summary>
    public class RasterRemovalCompatTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ClowdRasterCompatTests", Guid.NewGuid() + ".json");

        public void Dispose()
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch { }
        }

        // A raster-era settings file: the removed "RasterToolsEnabled" property, "Brush"/"Eraser"
        // in the toolbar lists, and a Tools dictionary mixing the removed "Brush" key with a
        // still-valid "Text" key.
        private const string RasterEraSettingsJson = @"{
  ""Editor"": {
    ""RasterToolsEnabled"": true,
    ""SidebarVisible"": true,
    ""ToolbarOrder"": [ ""Brush"", ""Rectangle"" ],
    ""HiddenTools"": [ ""Eraser"" ],
    ""Tools"": {
      ""Brush"": { ""LineWidth"": 7.5, ""AutoColor"": false },
      ""Text"": { ""FontFamily"": ""Consolas"", ""FontSize"": 16.0 }
    }
  }
}";

        private void WriteRasterEraSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, RasterEraSettingsJson);
        }

        [Fact]
        public void Load_RasterEraSettings_DoesNotThrow_DropsBrushKey_BindsTheRest()
        {
            WriteRasterEraSettings();

            var loaded = SettingsService.Load(_path);

            Assert.NotNull(loaded);
            // the removed "RasterToolsEnabled" property is silently ignored; a real property on the
            // same object still binds, proving the load did not abort on the unknown key
            Assert.True(loaded.Editor.SidebarVisible);
            // the unparseable "Brush" dictionary key is dropped while the valid "Text" entry binds
            var text = Assert.Contains(ToolType.Text, (IDictionary<ToolType, SavedToolSettings>)loaded.Editor.Tools);
            Assert.Equal("Consolas", text.FontFamily);
            Assert.Equal(16.0, text.FontSize);
            Assert.Single(loaded.Editor.Tools);
        }

        [Fact]
        public void ResolveToolbarOrder_ScrubsBrush_KeepsRectangleThenDefaults()
        {
            WriteRasterEraSettings();
            var loaded = SettingsService.Load(_path);

            var resolved = ToolbarConfig.ResolveToolbarOrder(loaded.Editor);

            var expected = new List<ToolType> { ToolType.Rectangle };
            expected.AddRange(ToolbarConfig.DefaultOrder.Where(t => t != ToolType.Rectangle));
            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void ResolveHiddenTools_ScrubsEraser_YieldsEmpty()
        {
            WriteRasterEraSettings();
            var loaded = SettingsService.Load(_path);

            var hidden = ToolbarConfig.ResolveHiddenTools(loaded.Editor);

            Assert.Empty(hidden);
        }
    }
}
