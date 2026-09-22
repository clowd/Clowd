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
            ToolType.Measure,
            ToolType.PolyLine,
            ToolType.Count,
            ToolType.Text,
            ToolType.Pixelate,
        };

        /// <summary>Resolves an effective toolbar order from persisted keys and the current
        /// defaults: keeps the persisted keys that are still known (dropping unknown ones —
        /// including stale ones from removed tools — and duplicates), then appends any default not
        /// already present, in default order. A null/empty persisted list returns the defaults.
        /// The video editor's strip is keyed by name rather than by <see cref="ToolType"/>, so the
        /// rule lives here once and both editors' resolvers are a call to it.</summary>
        public static IReadOnlyList<string> ResolveOrder(IReadOnlyList<string> persisted, IReadOnlyList<string> defaults)
        {
            if (persisted == null || persisted.Count == 0)
                return defaults;

            var known = new HashSet<string>(defaults, StringComparer.Ordinal);
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var key in persisted)
            {
                if (key == null || !known.Contains(key) || !seen.Add(key))
                    continue;
                result.Add(key);
            }

            foreach (var key in defaults)
            {
                if (seen.Add(key))
                    result.Add(key);
            }

            return result;
        }

        /// <summary>Resolves a hidden set from persisted keys: unknown keys are dropped, and so is
        /// anything in <paramref name="alwaysVisible"/> — a tool the strip cannot do without
        /// cannot be hidden however the settings file got that way.</summary>
        public static ISet<string> ResolveHidden(IReadOnlyList<string> persisted, IReadOnlyList<string> known,
            IReadOnlyList<string> alwaysVisible = null)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (persisted == null)
                return result;

            var allowed = new HashSet<string>(known, StringComparer.Ordinal);
            if (alwaysVisible != null)
                allowed.ExceptWith(alwaysVisible);

            foreach (var key in persisted)
            {
                if (key != null && allowed.Contains(key))
                    result.Add(key);
            }

            return result;
        }

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
