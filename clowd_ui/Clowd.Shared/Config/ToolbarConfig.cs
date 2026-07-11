using System;
using System.Collections.Generic;

namespace Clowd.Config
{
    /// <summary>Resolves the editor toolbar tool order and hidden-tool set from the persisted
    /// (and potentially stale/invalid) settings, tolerating unknown enum names.</summary>
    public static class ToolbarConfig
    {
        /// <summary>The current default editor toolbar order.</summary>
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

        /// <summary>Resolves the effective toolbar order: leniently parses the persisted names
        /// (dropping unknown names — including stale ones from removed tools — and duplicates) then
        /// appends any default tool not already present, in default order. Null/empty persisted
        /// order returns the default.</summary>
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
        /// hidden and unknown names are dropped.</summary>
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
                result.Add(tool);
            }

            return result;
        }
    }
}
