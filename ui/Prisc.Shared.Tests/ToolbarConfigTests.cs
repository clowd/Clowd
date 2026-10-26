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
            // "Raster"/"Eraser" stand in for ToolType members of the removed raster v1 build that may
            // linger in persisted settings; they must drop silently ("Raster" no longer parses, and
            // "Eraser" is live again only as the overlay-only Draw on Screen eraser, which the
            // resolver scrubs; the raster era's "Brush" is the vector brush tool now, so it stays)
            var editor = new SettingsEditor { ToolbarOrder = new List<string> { "Raster", "Eraser", "Rectangle" } };
            var resolved = ToolbarConfig.ResolveToolbarOrder(editor);

            var expected = new List<ToolType> { ToolType.Rectangle };
            expected.AddRange(ToolbarConfig.DefaultOrder.Where(t => t != ToolType.Rectangle));
            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void ResolveToolbarOrder_LegacyPencilName_StillParses_EditorDropsIt()
        {
            // ToolType.PolyLine stays in the enum (names are persisted) although no tool backs it
            // any more; the resolver keeps it and the EDITOR's registry lookup is what drops it
            var editor = new SettingsEditor { ToolbarOrder = new List<string> { "PolyLine", "Pen", "Pointer" } };
            var resolved = ToolbarConfig.ResolveToolbarOrder(editor).ToList();

            Assert.Equal(ToolType.PolyLine, resolved[0]);
            Assert.Equal(ToolType.Pen, resolved[1]);
            Assert.Equal(ToolType.Pointer, resolved[2]);
        }

        [Fact]
        public void DefaultOrder_HasThePenAndTheBrush_NotTheLegacyPencil()
        {
            Assert.Contains(ToolType.Pen, ToolbarConfig.DefaultOrder);
            Assert.Contains(ToolType.Brush, ToolbarConfig.DefaultOrder);
            Assert.DoesNotContain(ToolType.PolyLine, ToolbarConfig.DefaultOrder);

            // the two take the pencil's one slot, in that order
            var order = ToolbarConfig.DefaultOrder.ToList();
            Assert.Equal(order.IndexOf(ToolType.Pen) + 1, order.IndexOf(ToolType.Brush));
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
            var editor = new SettingsEditor { HiddenTools = new List<string> { "Raster", "Eraser" } };
            Assert.Empty(ToolbarConfig.ResolveHiddenTools(editor));
        }
    }
}
