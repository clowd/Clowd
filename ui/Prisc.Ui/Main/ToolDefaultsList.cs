using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prisc.Config;
using Prisc.Drawing;
using Prisc.UI.Controls;
using Prisc.UI.Converters;
using Prisc.UI.Dialogs.ColorPicker;
using Prisc.UI.Helpers;

namespace Prisc.UI.Config
{
    /// <summary>
    /// The image editor's per-tool styles as a settings list: one row per drawing tool, in toolbar
    /// order, showing at a glance the style a new graphic gets (a swatch, the stroke, the font...).
    /// A row expands in place (one at a time) into the same fields the tool's property bar shows
    /// (built by <see cref="ToolStyleFields"/>, laid out as a form), each with the editors' reset
    /// dot, and a modified row offers to reset the whole tool.
    /// </summary>
    /// <remarks>
    /// The rows read <see cref="SettingsEditor.Tools"/> without creating entries: a tool the user
    /// never touched is shown from <see cref="SavedToolSettings.CreateDefault"/> and only gains an
    /// entry when its row is opened for editing. Opening the page alone must not pin today's
    /// defaults into the settings file, where a tool would no longer follow a future default.
    /// Entries are edited and reset in place, so an editor window bound to the same instance
    /// follows live.
    /// </remarks>
    public class ToolDefaultsList : Panel
    {
        public static readonly DirectProperty<ToolDefaultsList, bool> HasChangesProperty =
            AvaloniaProperty.RegisterDirect<ToolDefaultsList, bool>(nameof(HasChanges), o => o.HasChanges);

        /// <summary>True while any tool's style differs from its default.</summary>
        public bool HasChanges
        {
            get => _hasChanges;
            private set => SetAndRaise(HasChangesProperty, ref _hasChanges, value);
        }

        private readonly SettingsEditor _editor;
        private readonly StackPanel _stack = new();
        private readonly List<ToolRow> _rows = new();
        private readonly Popup _colorPopup;
        private readonly MiniColorDialog _miniColor;
        private Dictionary<ToolType, SavedToolSettings> _boundTools;
        private bool _hasChanges;

        public ToolDefaultsList(SettingsEditor editor)
        {
            _editor = editor;
            HorizontalAlignment = HorizontalAlignment.Left;
            MaxWidth = 720;
            AddFieldStyles();

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Child = _stack,
            };
            card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("SemiColorBorder"));
            card.Bind(Border.BackgroundProperty, card.GetResourceObservable("ButtonDefaultBackground"));
            Children.Add(card);

            // the editors' in-place picker, realtime so an open editor's bar follows every move
            _miniColor = new MiniColorDialog { Margin = new Thickness(10), Realtime = true };
            _colorPopup = new Popup
            {
                IsLightDismissEnabled = true,
                Placement = PlacementMode.BottomEdgeAlignedLeft,
                HorizontalOffset = -10,
                VerticalOffset = -8,
                Child = _miniColor,
            };
            _miniColor.Canceled += (_, _) => _colorPopup.IsOpen = false;
            Children.Add(_colorPopup);
        }

        /// <summary>The sizes the editor's chrome styles give the shared fields (see
        /// <see cref="ToolStyleFields"/>), at the settings page's scale: Semi's own controls (the
        /// modern variant of each), wide enough to line up as a form column.</summary>
        private void AddFieldStyles()
        {
            Styles.Add(new Style(x => x.OfType<ThemedSpinner>())
            {
                Setters = { new Setter(ThemedSpinner.IsModernProperty, true), new Setter(WidthProperty, 130d) },
            });
            Styles.Add(new Style(x => x.OfType<ThemedDropDown>())
            {
                Setters = { new Setter(ThemedDropDown.IsModernProperty, true), new Setter(MinWidthProperty, 130d) },
            });
            Styles.Add(new Style(x => x.OfType<Button>().Class("fieldButton"))
            {
                Setters = { new Setter(MinWidthProperty, 130d), new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Left) },
            });
            Styles.Add(new Style(x => x.OfType<CaptionedCheckBox>())
            {
                Setters = { new Setter(CaptionedCheckBox.CornerRadiusProperty, new CornerRadius(4)) },
            });
            Styles.Add(new Style(x => x.OfType<Border>().Class("swatchOuter"))
            {
                Setters =
                {
                    new Setter(WidthProperty, 28d),
                    new Setter(HeightProperty, 28d),
                    new Setter(Border.CornerRadiusProperty, new CornerRadius(4)),
                    new Setter(MarginProperty, new Thickness(0)),
                },
            });
            Styles.Add(new Style(x => x.OfType<Border>().Class("colorSwatch"))
            {
                Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(3.5)) },
            });
            Styles.Add(new Style(x => x.OfType<Border>().Class("colorFill"))
            {
                Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(3)) },
            });
            Styles.Add(new Style(x => x.OfType<Border>().Class("colorSwatch").Class(":focus-visible"))
            {
                Setters = { new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AdornerLayerBorderBrush")) },
            });
        }

        /// <summary>Opens the settings window on the editor page, scrolled to the tool list, with
        /// <paramref name="tool"/>'s row open when it has one (the image editor's "Edit Tool
        /// Defaults", passing the tool in hand).</summary>
        public static void ShowInSettings(ToolType? tool = null)
        {
            var page = PageManager.Current.GetSettingsPage();
            page.Open(SettingsPageTab.SettingsEditor);
            if (page is not Visual root)
                return;

            // posted: a page opened just now is not attached and laid out yet, and the rows are
            // built on attach
            Dispatcher.UIThread.Post(() =>
            {
                if (root.GetVisualDescendants().OfType<ToolDefaultsList>().FirstOrDefault() is not { } list)
                    return;

                if (tool is { } t && list._rows.FirstOrDefault(r => r.Tool == t) is { } row)
                    row.SetExpanded(true);
                list.BringIntoView();
            }, DispatcherPriority.Background);
        }

        /// <summary>The "Reset all" button for the row this list sits under: enabled while any
        /// tool differs from its default.</summary>
        public Button CreateResetAllButton()
        {
            var button = new Button { Content = "Reset all…" };
            button.Classes.Add("Danger");
            button.Bind(IsEnabledProperty, this.GetObservable(HasChangesProperty));
            button.Click += async (_, _) => await ResetAllAsync();
            return button;
        }

        private async Task ResetAllAsync()
        {
            if (!await NiceDialog.ShowYesNoPromptAsync(this, NiceDialogIcon.Warning,
                    "Reset every tool's saved color, line width and font back to the defaults? This cannot be undone."))
                return;

            // in place, entry by entry: an open editor's property bar is bound to these instances
            foreach (var (tool, settings) in _editor.Tools.ToList())
                settings?.ResetToDefault(tool);

            foreach (var row in _rows)
                row.Refresh();
            _editor.NotifyToolSettingsChanged();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _miniColor.ParentWindow = TopLevel.GetTopLevel(this) as Window;
            _editor.PropertyChanged += OnEditorPropertyChanged;

            // rebuilt on every visit: an editor window may have created or changed entries while
            // the page was away, and the rows only watch the entries that existed when built
            Rebuild();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _editor.PropertyChanged -= OnEditorPropertyChanged;
            foreach (var row in _rows)
                row.Detach();
            base.OnDetachedFromVisualTree(e);
        }

        private void OnEditorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // a change to the entries themselves comes through here too (NotifyToolSettingsChanged)
            // and needs nothing; only a replaced dictionary (an editor's own "reset all") orphans
            // the rows
            if (e.PropertyName is null or nameof(SettingsEditor.Tools) && !ReferenceEquals(_boundTools, _editor.Tools))
                Rebuild();
        }

        private void Rebuild()
        {
            var expanded = _rows.FirstOrDefault(r => r.IsExpanded)?.Tool;
            foreach (var row in _rows)
                row.Detach();
            _rows.Clear();
            _stack.Children.Clear();
            _boundTools = _editor.Tools;

            foreach (var tool in ToolbarConfig.ResolveToolbarOrder(_editor))
            {
                var skills = DrawingCanvas.GetToolSettingSkills(tool);
                var fields = ToolStyleFields.All.Where(f => skills.HasFlag(f.Skill)).ToArray();
                if (fields.Length == 0 || EditorWindow.GetToolLabel(tool) is not { } label)
                    continue;

                var row = new ToolRow(this, tool, label.DisplayName, label.IconKey, skills, fields);
                if (_stack.Children.Count > 0)
                    row.Root.BorderThickness = new Thickness(0, 1, 0, 0);
                _rows.Add(row);
                _stack.Children.Add(row.Root);
            }

            if (expanded is { } t && _rows.FirstOrDefault(r => r.Tool == t) is { } again)
                again.SetExpanded(true);

            UpdateHasChanges();
        }

        private void UpdateHasChanges() => HasChanges = _rows.Any(r => r.IsModified);

        /// <summary>Accordion: opening one row closes whichever was open.</summary>
        private void OnRowExpanding(ToolRow row)
        {
            foreach (var other in _rows)
                if (!ReferenceEquals(other, row) && other.IsExpanded)
                    other.SetExpanded(false);
        }

        private void OpenColorPicker(Control anchor, Color current, Action<Color> write)
        {
            _colorPopup.PlacementTarget = anchor;
            _miniColor.Reset(current, write);
            _colorPopup.IsOpen = true;
        }

        /// <summary>
        /// One tool: a header (icon, name, style summary, reset, chevron) and the fields it opens
        /// into. Holds no settings instance until it has to — see the class remarks.
        /// </summary>
        private sealed class ToolRow
        {
            public ToolType Tool { get; }
            public Border Root { get; }
            public bool IsExpanded { get; private set; }
            public bool IsModified { get; private set; }

            private readonly ToolDefaultsList _owner;
            private readonly Skill _skills;
            private readonly ToolStyleField[] _fields;
            private readonly ToolStyleFields _defaults;
            private readonly StackPanel _summary;
            private readonly Button _reset;
            private readonly RotateTransform _chevronRotation = new();
            private readonly Border _body;
            private readonly Dictionary<ToolStyleField, ResetDefaultButton> _dots = new();
            private SavedToolSettings _watched;

            public ToolRow(ToolDefaultsList owner, ToolType tool, string name, string iconKey, Skill skills, ToolStyleField[] fields)
            {
                _owner = owner;
                Tool = tool;
                _skills = skills;
                _fields = fields;
                _defaults = For(SavedToolSettings.CreateDefault(tool));

                var icon = new PathIcon { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
                if (owner.TryFindResource(iconKey, out var geometry) && geometry is Geometry g)
                    icon.Data = g;

                var title = new TextBlock
                {
                    Text = name,
                    MinWidth = 130,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 12, 0),
                };

                _summary = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    ClipToBounds = true,
                };

                _reset = new Button { Content = "Reset", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, -4, 4, -4) };
                _reset.Classes.Add("Tertiary");
                ToolTip.SetTip(_reset, $"Put {name.ToLowerInvariant()} back to its default style");
                _reset.Click += (_, e) =>
                {
                    e.Handled = true; // not a header click
                    Resolve(create: false)?.ResetToDefault(Tool);
                    Refresh();
                    _owner._editor.NotifyToolSettingsChanged();
                };

                var chevron = new PathIcon
                {
                    Width = 10,
                    Height = 10,
                    Opacity = 0.65,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = _chevronRotation,
                };
                if (owner.TryFindResource("IconChevronDown", out var chevronGeometry) && chevronGeometry is Geometry cg)
                    chevron.Data = cg;

                var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
                headerGrid.Children.Add(icon);
                headerGrid.Children.Add(title);
                headerGrid.Children.Add(_summary);
                headerGrid.Children.Add(_reset);
                headerGrid.Children.Add(chevron);
                for (int i = 0; i < headerGrid.Children.Count; i++)
                    Grid.SetColumn(headerGrid.Children[i], i);

                var header = new Border
                {
                    Padding = new Thickness(14, 10, 14, 10),
                    MinHeight = 48,
                    Background = Brushes.Transparent,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Focusable = true,
                    Child = headerGrid,
                };
                Avalonia.Automation.AutomationProperties.SetName(header, name);
                IDisposable hover = null;
                header.PointerEntered += (_, _) => hover = header.Bind(Border.BackgroundProperty, header.GetResourceObservable("ButtonDefaultPointeroverBackground"));
                header.PointerExited += (_, _) =>
                {
                    hover?.Dispose();
                    header.Background = Brushes.Transparent;
                };
                header.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                        SetExpanded(!IsExpanded);
                };
                header.KeyDown += (_, e) =>
                {
                    if (e.Key is Key.Enter or Key.Space)
                    {
                        e.Handled = true;
                        SetExpanded(!IsExpanded);
                    }
                };

                // indented to the title, so the fields read as belonging to the tool above them
                _body = new Border { Padding = new Thickness(44, 2, 16, 16), IsVisible = false };

                var stack = new StackPanel();
                stack.Children.Add(header);
                stack.Children.Add(_body);
                Root = new Border { Child = stack };
                Root.Bind(Border.BorderBrushProperty, Root.GetResourceObservable("SemiColorBorder"));

                Watch(Resolve(create: false));
                Refresh();
            }

            /// <summary>The shared field builder over one settings instance.</summary>
            private ToolStyleFields For(SavedToolSettings settings)
                => new(settings, ToolStyleFields.SettingsPaths,
                       skill => new Binding { Source = _skills.HasFlag(skill), Mode = BindingMode.OneTime },
                       _owner.OpenColorPicker);

            /// <summary>This tool's saved entry; with <paramref name="create"/>, made on demand.</summary>
            private SavedToolSettings Resolve(bool create)
            {
                if (create)
                    return _owner._editor.GetToolSettings(Tool);
                return _owner._editor.Tools.TryGetValue(Tool, out var s) ? s : null;
            }

            private ToolStyleFields Current => Resolve(create: false) is { } s ? For(s) : _defaults;

            public void SetExpanded(bool expanded)
            {
                if (expanded == IsExpanded)
                    return;

                IsExpanded = expanded;
                if (expanded)
                {
                    _owner.OnRowExpanding(this);

                    // editing needs the real entry (an editor window may also have made it since)
                    var settings = Resolve(create: true);
                    Watch(settings);
                    _body.Child = BuildForm(For(settings));
                    Refresh();
                }
                else
                {
                    _body.Child = null;
                    _dots.Clear();
                }

                _body.IsVisible = expanded;
                _summary.IsVisible = !expanded; // the fields say it all, and in full
                _chevronRotation.Angle = expanded ? 180 : 0;
            }

            private void Watch(SavedToolSettings settings)
            {
                if (ReferenceEquals(settings, _watched))
                    return;
                Detach();
                _watched = settings;
                if (_watched != null)
                    _watched.PropertyChanged += OnSettingChanged;
            }

            public void Detach()
            {
                if (_watched != null)
                    _watched.PropertyChanged -= OnSettingChanged;
                _watched = null;
            }

            private void OnSettingChanged(object sender, PropertyChangedEventArgs e)
            {
                Refresh();
                _owner._editor.NotifyToolSettingsChanged();
            }

            private bool IsFieldDefault(ToolStyleFields current, ToolStyleField field)
                => field.Values.All(v => Equals(current.Get(v), _defaults.Get(v)));

            /// <summary>Brings the summary, the reset button and the field dots up to date.</summary>
            public void Refresh()
            {
                var current = Current;
                IsModified = !_fields.All(f => IsFieldDefault(current, f));
                _reset.IsVisible = IsModified;

                foreach (var (field, dot) in _dots)
                    dot.CurrentValue = IsFieldDefault(current, field); // shown while it differs from DefaultValue (true)

                BuildSummary(current);
                _owner.UpdateHasChanges();
            }

            // ---- collapsed: the summary ---------------------------------------------------

            private void BuildSummary(ToolStyleFields s)
            {
                _summary.Children.Clear();
                var parts = new List<string>();

                foreach (var field in _fields)
                {
                    switch (field.Skill)
                    {
                        case Skill.Color:
                            _summary.Children.Add(Swatch((Color)s.Get(StyleValue.Color)));
                            break;
                        case Skill.Fill when _skills.HasFlag(Skill.AutoFill) && s.Get(StyleValue.AutoFill) is true:
                            parts.Add("auto fill");
                            break;
                        case Skill.Fill:
                            _summary.Children.Add(Swatch((Color)s.Get(StyleValue.Fill)));
                            break;
                        default:
                            parts.Add(ValueText(s, field));
                            break;
                    }
                }

                if (parts.Count > 0)
                {
                    _summary.Children.Add(new TextBlock
                    {
                        Text = String.Join(" · ", parts),
                        Opacity = 0.65,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                }
            }

            /// <summary>A field's value in words, for the summary and the reset dot's tip.</summary>
            private string ValueText(ToolStyleFields s, ToolStyleField field)
            {
                switch (field.Skill)
                {
                    case Skill.Color:
                        return ColorTextHelper.GetHex((Color)s.Get(StyleValue.Color));
                    case Skill.Fill:
                        return _skills.HasFlag(Skill.AutoFill) && s.Get(StyleValue.AutoFill) is true
                            ? "auto"
                            : ColorTextHelper.GetHex((Color)s.Get(StyleValue.Fill));
                    case Skill.Stroke:
                        return $"{s.Get(StyleValue.Stroke):0.#} px";
                    case Skill.Font:
                    {
                        var text = $"{s.Get(StyleValue.FontFamily)} {s.Get(StyleValue.FontSize):0.#}";
                        if (s.Get(StyleValue.FontWeight) is FontWeight weight && weight != FontWeight.Normal)
                            text += " " + weight.ToString().ToLowerInvariant();
                        if (s.Get(StyleValue.FontStyle) is FontStyle style && style != FontStyle.Normal)
                            text += " " + style.ToString().ToLowerInvariant();
                        return text;
                    }
                    case Skill.FontFamily:
                        return (string)s.Get(StyleValue.FontFamily);
                    case Skill.Scale:
                        return $"{s.Get(StyleValue.Scale):0.##}× size";
                    case Skill.BlurRadius:
                        return $"{s.Get(StyleValue.BlurRadius):0} px blur";
                    case Skill.Radius:
                        return $"{s.Get(StyleValue.CornerRadius):0} px radius";
                    case Skill.DashStyle:
                        return s.Get(StyleValue.DashStyle).ToString()!.ToLowerInvariant();
                    case Skill.ObscureMode:
                        return s.Get(StyleValue.ObscureMode).ToString()!.ToLowerInvariant();
                    default:
                        return field.Label;
                }
            }

            private static Border Swatch(Color color)
            {
                var frame = new Border
                {
                    Width = 14,
                    Height = 14,
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    Background = AppStyles.CheckerboardBrushSmall,
                    ClipToBounds = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(2) },
                };
                frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("SemiColorBorder"));
                return frame;
            }

            // ---- expanded: the form ------------------------------------------------------

            /// <summary>The shared fields as a form: label, control, and the editors' reset dot
            /// against this tool's default.</summary>
            private Control BuildForm(ToolStyleFields fields)
            {
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"),
                    RowSpacing = 8,
                };

                for (int i = 0; i < _fields.Length; i++)
                {
                    var field = _fields[i];
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                    var label = new TextBlock
                    {
                        Text = field.Label,
                        MinWidth = 86,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 16, 0),
                    };
                    var editor = fields.BuildEditor(field);
                    editor.HorizontalAlignment = HorizontalAlignment.Left;

                    // compares "this field is at its default" with true, so it shows while it is not
                    var dot = new ResetDefaultButton
                    {
                        DefaultValue = true,
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Command = new RelayCommand
                        {
                            Executed = _ =>
                            {
                                foreach (var value in field.Values)
                                    fields.Set(value, _defaults.Get(value));
                            },
                        },
                    };
                    ToolTip.SetTip(dot, $"Reset to default ({ValueText(_defaults, field)})");
                    _dots[field] = dot;

                    Grid.SetRow(label, i);
                    Grid.SetRow(editor, i);
                    Grid.SetRow(dot, i);
                    Grid.SetColumn(editor, 1);
                    Grid.SetColumn(dot, 2);
                    grid.Children.Add(label);
                    grid.Children.Add(editor);
                    grid.Children.Add(dot);
                }

                return grid;
            }
        }
    }
}
