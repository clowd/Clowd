using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The two-column label/editor form behind a generated settings page. Children are tagged with
    /// <see cref="RoleProperty"/>: a Label starts a row, the Editor that follows sits beside it in
    /// the second column, and an optional Caption spans both columns underneath. All labels share
    /// one column, like a Grid with an Auto column, but here the column is allowed to give way.
    /// </summary>
    /// <remarks>
    /// A Grid measures its Auto column with infinite width, so a single long label ("Render
    /// automatically when capture finished") sets the column for every row, and whatever the star
    /// column has left is what the editors get - when that is less than they need they run off the
    /// right edge and are clipped. This panel resolves the width in two steps instead:
    /// <list type="number">
    /// <item>Room for everything: the label column is as wide as its widest label and every editor
    /// gets the width it asked for.</item>
    /// <item>Short of room: the label column narrows first (labels wrap) so the editors move left,
    /// but not below <see cref="LabelFloorFraction"/> of the width (or <see cref="LabelFloorMin"/>);
    /// past that the editors take the shortfall and shrink, which they can do because they carry a
    /// <see cref="PreferredWidthBox"/> rather than a MinWidth.</item>
    /// </list>
    /// A label that cannot wrap as narrow as the column (one long word) widens the column to fit.
    /// Invisible children (a [VisibleWhen] row that is switched off) measure to zero and so take
    /// no height and no part in the column width.
    /// </remarks>
    public class SettingsRowsPanel : Panel
    {
        public enum Role
        {
            Label,
            Editor,
            Caption,
        }

        public static readonly AttachedProperty<Role> RoleProperty =
            AvaloniaProperty.RegisterAttached<SettingsRowsPanel, Control, Role>("Role");

        public static Role GetRole(Control control)
        {
            return control.GetValue(RoleProperty);
        }

        public static void SetRole(Control control, Role value)
        {
            control.SetValue(RoleProperty, value);
        }

        /// <summary>The label column never narrows below this share of the panel width; past it,
        /// editors shrink instead. Labels wrapping onto two lines reads fine, labels wrapping onto
        /// five does not.</summary>
        public const double LabelFloorFraction = 0.4;

        /// <summary>Nor below this many pixels, whatever the share works out to.</summary>
        public const double LabelFloorMin = 120;

        private sealed class Row
        {
            public Control Label;
            public Control Editor;
            public Control Caption;
        }

        private double _labelColumn;

        private List<Row> CollectRows()
        {
            var rows = new List<Row>();
            Row current = null;

            foreach (var child in Children)
            {
                var role = GetRole(child);
                if (role == Role.Label || current == null)
                {
                    current = new Row();
                    rows.Add(current);
                }

                switch (role)
                {
                    case Role.Label:
                        current.Label = child;
                        break;
                    case Role.Editor:
                        current.Editor = child;
                        break;
                    case Role.Caption:
                        current.Caption = child;
                        break;
                }
            }

            return rows;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var rows = CollectRows();
            var width = availableSize.Width;
            var constrained = !Double.IsInfinity(width);
            var unbounded = new Size(Double.PositiveInfinity, Double.PositiveInfinity);

            // pass 1: what every label and editor would take given all the room in the world.
            double labelNatural = 0, editorPreferred = 0;
            foreach (var row in rows)
            {
                if (row.Label != null)
                {
                    row.Label.Measure(unbounded);
                    labelNatural = Math.Max(labelNatural, row.Label.DesiredSize.Width);
                }

                if (row.Editor != null)
                {
                    row.Editor.Measure(unbounded);
                    editorPreferred = Math.Max(editorPreferred, row.Editor.DesiredSize.Width);
                }
            }

            // step 2: short of room, the label column gives way first, down to its floor.
            var labelColumn = labelNatural;
            if (constrained && labelNatural + editorPreferred > width)
            {
                var floor = Math.Min(labelNatural, Math.Max(width * LabelFloorFraction, LabelFloorMin));
                labelColumn = Math.Max(floor, width - editorPreferred);
            }

            // labels re-measured at the column width wrap to it - or, unable to, widen it.
            var labelActual = 0d;
            foreach (var row in rows)
            {
                if (row.Label == null)
                    continue;

                row.Label.Measure(new Size(labelColumn, Double.PositiveInfinity));
                labelActual = Math.Max(labelActual, row.Label.DesiredSize.Width);
            }

            labelColumn = Math.Max(labelColumn, labelActual);
            _labelColumn = labelColumn;

            var editorColumn = constrained ? Math.Max(0, width - labelColumn) : editorPreferred;
            var captionWidth = constrained ? width : Double.PositiveInfinity;

            double height = 0, editorActual = 0, captionActual = 0;
            foreach (var row in rows)
            {
                var rowHeight = row.Label?.DesiredSize.Height ?? 0;

                if (row.Editor != null)
                {
                    row.Editor.Measure(new Size(editorColumn, Double.PositiveInfinity));
                    editorActual = Math.Max(editorActual, row.Editor.DesiredSize.Width);
                    rowHeight = Math.Max(rowHeight, row.Editor.DesiredSize.Height);
                }

                height += rowHeight;

                if (row.Caption != null)
                {
                    row.Caption.Measure(new Size(captionWidth, Double.PositiveInfinity));
                    captionActual = Math.Max(captionActual, row.Caption.DesiredSize.Width);
                    height += row.Caption.DesiredSize.Height;
                }
            }

            return new Size(Math.Max(labelColumn + editorActual, captionActual), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var rows = CollectRows();
            var labelColumn = Math.Min(_labelColumn, finalSize.Width);
            var editorColumn = Math.Max(0, finalSize.Width - labelColumn);

            double y = 0;
            foreach (var row in rows)
            {
                var rowHeight = Math.Max(row.Label?.DesiredSize.Height ?? 0, row.Editor?.DesiredSize.Height ?? 0);

                row.Label?.Arrange(new Rect(0, y, labelColumn, rowHeight));
                row.Editor?.Arrange(new Rect(labelColumn, y, editorColumn, rowHeight));
                y += rowHeight;

                if (row.Caption != null)
                {
                    var captionHeight = row.Caption.DesiredSize.Height;
                    row.Caption.Arrange(new Rect(0, y, finalSize.Width, captionHeight));
                    y += captionHeight;
                }
            }

            return new Size(finalSize.Width, y);
        }
    }
}
