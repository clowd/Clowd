using System.Collections.Generic;
using System.Linq;
using Clowd;
using Clowd.Config;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class ToolbarConfigTests
    {
        [Fact]
        public void ResolveToolbarOrder_NullOrder_ReturnsDefault()
        {
            var editor = new SettingsEditor { ToolbarOrder = null };
            Assert.Equal(ToolbarConfig.DefaultOrder, ToolbarConfig.ResolveToolbarOrder(editor));
        }

        [Fact]
        public void ResolveToolbarOrder_EmptyOrder_ReturnsDefault()
        {
            var editor = new SettingsEditor { ToolbarOrder = new List<string>() };
            Assert.Equal(ToolbarConfig.DefaultOrder, ToolbarConfig.ResolveToolbarOrder(editor));
        }

        [Fact]
        public void ResolveToolbarOrder_UnknownNamesDropped_AndDefaultsAppended()
        {
            var editor = new SettingsEditor { ToolbarOrder = new List<string> { "Rectangle", "Bogus", "Rectangle" } };
            var resolved = ToolbarConfig.ResolveToolbarOrder(editor);

            // Rectangle first (deduped, unknown dropped), then the remaining defaults in order.
            var expected = new List<ToolType> { ToolType.Rectangle };
            expected.AddRange(ToolbarConfig.DefaultOrder.Where(t => t != ToolType.Rectangle));

            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void ResolveToolbarOrder_Reorder_PreservesPersistedThenAppendsMissing()
        {
            var editor = new SettingsEditor { ToolbarOrder = new List<string> { "Text", "Pointer" } };
            var resolved = ToolbarConfig.ResolveToolbarOrder(editor).ToList();

            Assert.Equal(ToolType.Text, resolved[0]);
            Assert.Equal(ToolType.Pointer, resolved[1]);
            // every default tool is present exactly once
            Assert.Equal(ToolbarConfig.DefaultOrder.OrderBy(t => t), resolved.OrderBy(t => t));
            Assert.Equal(resolved.Count, resolved.Distinct().Count());
        }

        [Fact]
        public void ResolveToolbarOrder_StaleRasterEraNames_DroppedAsUnknown()
        {
            // "Brush"/"Eraser" were ToolType members in the removed raster v1 build and may
            // linger in persisted settings; they no longer parse and must drop silently
            var editor = new SettingsEditor { ToolbarOrder = new List<string> { "Brush", "Eraser", "Rectangle" } };
            var resolved = ToolbarConfig.ResolveToolbarOrder(editor);

            var expected = new List<ToolType> { ToolType.Rectangle };
            expected.AddRange(ToolbarConfig.DefaultOrder.Where(t => t != ToolType.Rectangle));
            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void ResolveHiddenTools_NullList_ReturnsEmpty()
        {
            var editor = new SettingsEditor { HiddenTools = null };
            Assert.Empty(ToolbarConfig.ResolveHiddenTools(editor));
        }

        [Fact]
        public void ResolveHiddenTools_LenientParse_DropsUnknownAndPointer()
        {
            var editor = new SettingsEditor { HiddenTools = new List<string> { "Rectangle", "Bogus", "Pointer" } };
            var hidden = ToolbarConfig.ResolveHiddenTools(editor);

            Assert.Contains(ToolType.Rectangle, hidden);
            Assert.DoesNotContain(ToolType.Pointer, hidden);
            Assert.Single(hidden);
        }

        [Fact]
        public void ResolveHiddenTools_StaleRasterEraNames_DroppedAsUnknown()
        {
            var editor = new SettingsEditor { HiddenTools = new List<string> { "Brush", "Eraser" } };
            Assert.Empty(ToolbarConfig.ResolveHiddenTools(editor));
        }
    }
}
