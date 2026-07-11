using System;
using System.IO;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Config;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class SettingsRoundTripTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ClowdSettingsTests", Guid.NewGuid() + ".json");

        public void Dispose()
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch { }
        }

        [Fact]
        public void Load_WithMissingFile_ReturnsDefaults()
        {
            var loaded = SettingsService.Load(_path);

            Assert.NotNull(loaded);
            Assert.True(loaded.General.ConfirmClose);
            Assert.Equal(new SimpleKeyGesture(Key.Snapshot), loaded.Hotkeys.CaptureRegionShortcut);
            Assert.Equal(Colors.Transparent, loaded.Editor.CanvasBackground);
            Assert.Equal(0.80, loaded.Capture.ObscuredWindowDetectionThreshold);
            Assert.Empty(loaded.Editor.Tools);
        }

        [Fact]
        public void SaveThenLoad_PreservesNonDefaultGraph()
        {
            var original = new SettingsRoot();

            // strings / bools / numbers
            original.General.LastSavePath = @"C:\Users\test\Pictures";
            original.General.ConfirmClose = false;
            original.Capture.ScreenshotWithCursor = false;
            original.Capture.TipsMode = CapturerTipsMode.Off; // enum by name
            original.Capture.ObscuredWindowDetectionThreshold = 0.55; // invariant double
            original.Editor.StartupPadding = 42;

            // Color ("#AARRGGBB")
            original.Editor.CanvasBackground = Color.FromArgb(0x80, 0x12, 0x34, 0x56);

            // nested object (TimeOption)
            original.Editor.DeleteSessionsAfter = new TimeOption(2, TimeOptionUnit.Weeks);

            // Tools dictionary entry (enum key) with Color + font enums
            var tool = original.Editor.GetToolSettings(ToolType.Text);
            tool.ObjectColor = Colors.Lime;
            tool.AutoColor = false;
            tool.LineWidth = 7.5;
            tool.FontWeight = FontWeight.Bold;
            tool.FontStyle = FontStyle.Italic;
            tool.FontStretch = FontStretch.Condensed;
            tool.FontFamily = "Consolas";

            // gestures: modified, and cleared (must NOT resurrect the compiled-in default)
            original.Hotkeys.CaptureRegionShortcut = new SimpleKeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift);
            original.Hotkeys.FileUploadShortcut = new SimpleKeyGesture(Key.F12, KeyModifiers.Alt);
            original.Hotkeys.CaptureFullscreenShortcut = null; // default is Ctrl+PrtScr

            SettingsService.Save(original, _path);
            var loaded = SettingsService.Load(_path);

            Assert.Equal(@"C:\Users\test\Pictures", loaded.General.LastSavePath);
            Assert.False(loaded.General.ConfirmClose);
            Assert.False(loaded.Capture.ScreenshotWithCursor);
            Assert.Equal(CapturerTipsMode.Off, loaded.Capture.TipsMode);
            Assert.Equal(0.55, loaded.Capture.ObscuredWindowDetectionThreshold);
            Assert.Equal(42, loaded.Editor.StartupPadding);

            Assert.Equal(Color.FromArgb(0x80, 0x12, 0x34, 0x56), loaded.Editor.CanvasBackground);

            Assert.Equal(2, loaded.Editor.DeleteSessionsAfter.Number);
            Assert.Equal(TimeOptionUnit.Weeks, loaded.Editor.DeleteSessionsAfter.Unit);

            var loadedTool = Assert.Contains(ToolType.Text, (System.Collections.Generic.IDictionary<ToolType, SavedToolSettings>)loaded.Editor.Tools);
            Assert.Equal(Colors.Lime, loadedTool.ObjectColor);
            Assert.False(loadedTool.AutoColor);
            Assert.Equal(7.5, loadedTool.LineWidth);
            Assert.Equal(FontWeight.Bold, loadedTool.FontWeight);
            Assert.Equal(FontStyle.Italic, loadedTool.FontStyle);
            Assert.Equal(FontStretch.Condensed, loadedTool.FontStretch);
            Assert.Equal("Consolas", loadedTool.FontFamily);

            Assert.Equal(new SimpleKeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift), loaded.Hotkeys.CaptureRegionShortcut);
            Assert.Equal(new SimpleKeyGesture(Key.F12, KeyModifiers.Alt), loaded.Hotkeys.FileUploadShortcut);
            Assert.Null(loaded.Hotkeys.CaptureFullscreenShortcut);

            // untouched values still come back as defaults
            Assert.Equal(new SimpleKeyGesture(Key.Snapshot, KeyModifiers.Alt), loaded.Hotkeys.CaptureActiveShortcut);
            Assert.True(loaded.Editor.RestoreSessionsOnClowdStart);
        }

        [Fact]
        public void EditorFeatureFlags_Default_WhenAbsentFromFile()
        {
            var loaded = SettingsService.Load(_path);

            Assert.False(loaded.Editor.SidebarVisible);
            Assert.Null(loaded.Editor.ToolbarOrder);
            Assert.Null(loaded.Editor.HiddenTools);
        }

        [Fact]
        public void EditorFeatureFlags_And_ToolbarLists_RoundTrip()
        {
            var original = new SettingsRoot();
            original.Editor.SidebarVisible = true;
            original.Editor.ToolbarOrder = new System.Collections.Generic.List<string> { "Rectangle", "Bogus", "Rectangle" };
            original.Editor.HiddenTools = new System.Collections.Generic.List<string> { "Ellipse", "Pointer" };

            SettingsService.Save(original, _path);
            var loaded = SettingsService.Load(_path);

            Assert.True(loaded.Editor.SidebarVisible);
            Assert.Equal(new[] { "Rectangle", "Bogus", "Rectangle" }, loaded.Editor.ToolbarOrder);
            Assert.Equal(new[] { "Ellipse", "Pointer" }, loaded.Editor.HiddenTools);

            // the lenient resolver tolerates the stale/duplicate names loaded from disk
            var resolved = ToolbarConfig.ResolveToolbarOrder(loaded.Editor);
            var expected = new System.Collections.Generic.List<ToolType> { ToolType.Rectangle };
            expected.AddRange(System.Linq.Enumerable.Where(ToolbarConfig.DefaultOrder, t => t != ToolType.Rectangle));
            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void SidebarWidth_Default_WhenAbsentFromFile()
        {
            var loaded = SettingsService.Load(_path);

            Assert.Equal(230d, loaded.Editor.SidebarWidth);
        }

        [Fact]
        public void SidebarWidth_RoundTrips()
        {
            var original = new SettingsRoot();
            original.Editor.SidebarWidth = 375;

            SettingsService.Save(original, _path);
            var loaded = SettingsService.Load(_path);

            Assert.Equal(375d, loaded.Editor.SidebarWidth);
        }

        [Fact]
        public void SimpleKeyGesture_SerializedString_RoundTrips()
        {
            // note: Key.Snapshot and Key.PrintScreen share a value — ToString() yields "PrintScreen",
            // and both names parse back to the same key.
            var gesture = new SimpleKeyGesture(Key.Snapshot, KeyModifiers.Control | KeyModifiers.Shift);
            Assert.Equal("Control+Shift+PrintScreen", gesture.ToSerializedString());
            Assert.Equal(gesture, SimpleKeyGesture.Parse(gesture.ToSerializedString()));
            Assert.Equal(gesture, SimpleKeyGesture.Parse("Control+Shift+Snapshot"));

            Assert.Null(SimpleKeyGesture.Parse(null));
            Assert.Null(SimpleKeyGesture.Parse(""));
            Assert.Null(SimpleKeyGesture.Parse("NotAKey"));
            Assert.Equal(new SimpleKeyGesture(Key.None), SimpleKeyGesture.Parse("None"));
        }
    }
}
