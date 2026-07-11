using System;
using System.Collections.Generic;

namespace Clowd.Config
{
    /// <summary>Resolves the editor toolbar tool order and hidden-tool set from the persisted
    /// (and potentially stale/invalid) settings, tolerating unknown enum names.</summary>
    public static class ToolbarConfig
    {
        /// <summary>The current default editor toolbar order. Raster tools are intentionally
        /// excluded — they are gated by <see cref="SettingsEditor.RasterToolsEnabled"/>.</summary>
        public static readonly IReadOnlyList<ToolType> DefaultOrder = new List<ToolType>
        {
            ToolType.None,
            ToolType.Pointer,
            ToolType.Rectangle,
            ToolType.FilledRectangle,
            ToolType.Ellipse,
            ToolType.Line,
            ToolType.Arrow,
            ToolType.PolyLine,
            ToolType.Count,
            ToolType.Text,
            ToolType.Pixelate,
        };

        public static bool IsRasterTool(ToolType t)
        {
            return t == ToolType.Brush || t == ToolType.Eraser;
        }

        /// <summary>Resolves the effective toolbar order: leniently parses the persisted names
        /// (dropping unknown names, duplicates and raster tools) then appends any default tool not
        /// already present, in default order. Null/empty persisted order returns the default.</summary>
        public static IReadOnlyList<ToolType> ResolveToolbarOrder(SettingsEditor editor)
        {
            var persisted = editor == null ? null : editor.ToolbarOrder;
            if (persisted == null || persisted.Count == 0)
                return DefaultOrder;

            var result = new List<ToolType>();
            var seen = new HashSet<ToolType>();

            foreach (var name in persisted)
            {
                if (!Enum.TryParse<ToolType>(name, out var tool))
                    continue;
                if (IsRasterTool(tool))
                    continue;
                if (!seen.Add(tool))
                    continue;
                result.Add(tool);
            }

            foreach (var tool in DefaultOrder)
            {
                if (seen.Add(tool))
                    result.Add(tool);
            }

            return result;
        }

        /// <summary>Resolves the set of hidden tools. <see cref="ToolType.Pointer"/> may never be
        /// hidden and raster tools are dropped (they are gated by the master flag).</summary>
        public static ISet<ToolType> ResolveHiddenTools(SettingsEditor editor)
        {
            var result = new HashSet<ToolType>();
            var persisted = editor == null ? null : editor.HiddenTools;
            if (persisted == null)
                return result;

            foreach (var name in persisted)
            {
                if (!Enum.TryParse<ToolType>(name, out var tool))
                    continue;
                if (tool == ToolType.Pointer)
                    continue;
                if (IsRasterTool(tool))
                    continue;
                result.Add(tool);
            }

            return result;
        }
    }
}
