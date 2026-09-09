using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Clowd.Config;
using Clowd.UI.Controls;
using Clowd.UI.Converters;
using Clowd.UI.Helpers;

namespace Clowd.UI.Config
{
    /// <summary>
    /// Builds a reflection-driven settings page (label + editor rows) for a settings category
    /// object. Direct port of the WPF SettingsControlFactory; WPF-only editors translated per
    /// the decision table (#53 NumberBox -> NumericUpDown + NumericTypeConverter, folder
    /// browser -> StorageProvider, AudioDeviceInfo rows dropped with the video feature).
    /// </summary>
    public class SettingsControlFactory
    {
        private readonly object _obj;
        private readonly Func<Window> _wndFn;

        public SettingsControlFactory(Func<Window> wndFn, object obj)
        {
            _obj = obj;
            _wndFn = wndFn;
        }

        private IEnumerable<PropertyDescriptor> GetObjectProperties(object obj)
        {
            var instanceType = obj.GetType();

            foreach (PropertyDescriptor pd in TypeDescriptor.GetProperties(obj))
            {
                if (pd.ComponentType != instanceType)
                    continue;

                if (!pd.IsBrowsable)
                    continue;

                if (OperatingSystem.IsMacOS() && GetFirstAttributeOrDefault<HiddenOnMacOSAttribute>(pd) != null)
                    continue;

                if (pd.IsReadOnly && GetFirstAttributeOrDefault<FlattenSettingsObjectAttribute>(pd) == null)
                    continue;

                yield return pd;
            }
        }

        public Control GetSettingsPanel()
        {
            var all = EnumerateRows().ToList();

            // a [ModeSelector] property is the page's headline choice: it renders as a row of tiles
            // above the sections (never inside one), and the sections are what it gates.
            var selectors = all.Where(r => GetFirstAttributeOrDefault<ModeSelectorAttribute>(r.Pd) != null).ToList();
            var rows = all.Except(selectors).ToList();

            // pages whose properties carry [Category] render as GroupBox sections (matching the
            // hand-written General page); pages without categories (Hotkeys, upload provider
            // settings) keep the flat list.
            var grouped = rows.Any(r => GetFirstAttributeOrDefault<CategoryAttribute>(r.Pd) != null);

            var content = grouped ? BuildGroupedPanel(rows) : BuildFlatPanel(rows);

            if (selectors.Count > 0)
            {
                var stack = new StackPanel { Spacing = 16 };
                foreach (var (owner, pd) in selectors)
                    stack.Children.Add(owner.BuildModeSelector(pd));
                stack.Children.Add(content);
                content = stack;
            }

            // margin on the *content* (not ScrollViewer padding) exactly like the hand-written
            // pages: the 16px right/bottom clearance then scrolls with the content, so the last
            // group keeps its gap at the bottom and the right edge never runs under the scrollbar.
            content.Margin = new Thickness(0, 0, 16, 16);

            return new FadeEdgeScrollViewer { Content = content };
        }

        /// <summary>All rows of this object in declaration order, with [FlattenSettingsObject]
        /// children expanded inline. The owning factory is kept so bindings target the right
        /// source object.</summary>
        private IEnumerable<(SettingsControlFactory Owner, PropertyDescriptor Pd)> EnumerateRows()
        {
            foreach (PropertyDescriptor pd in GetObjectProperties(_obj))
            {
                if (GetFirstAttributeOrDefault<FlattenSettingsObjectAttribute>(pd) != null)
                {
                    var child = new SettingsControlFactory(_wndFn, pd.GetValue(_obj));
                    foreach (var row in child.EnumerateRows())
                        yield return row;
                }
                else
                {
                    yield return (this, pd);
                }
            }
        }

        // the label column shares its width across the rows of one section (and gives way when
        // the window is short of room) - see SettingsRowsPanel for why a Grid does not do here.
        private static SettingsRowsPanel NewRowsPanel()
        {
            return new SettingsRowsPanel();
        }

        private Control BuildFlatPanel(List<(SettingsControlFactory Owner, PropertyDescriptor Pd)> rows)
        {
            var stack = new StackPanel { Spacing = 8 };
            var panel = NewRowsPanel();
            foreach (var (owner, pd) in rows)
                owner.AddRow(panel, pd);

            stack.Children.Add(panel);
            return stack;
        }

        private Control BuildGroupedPanel(List<(SettingsControlFactory Owner, PropertyDescriptor Pd)> rows)
        {
            var stack = new StackPanel { Spacing = 16 };

            // GroupBy preserves first-appearance order, so a category's rows collect together
            // even when declarations interleave.
            foreach (var group in rows.GroupBy(r => GetFirstAttributeOrDefault<CategoryAttribute>(r.Pd)?.Category ?? "Other"))
            {
                var panel = NewRowsPanel();
                foreach (var (owner, pd) in group)
                    owner.AddRow(panel, pd);

                var box = new HeaderedContentControl { Header = group.Key, Content = panel };
                var app = Application.Current;
                if (app != null && app.TryGetResource("GroupBox", app.ActualThemeVariant, out var theme) && theme is ControlTheme groupBoxTheme)
                    box.Theme = groupBoxTheme;

                GateGroupVisibility(box, group.ToList());
                stack.Children.Add(box);
            }

            return stack;
        }

        /// <summary>
        /// Hides a section while every one of its rows is hidden by [VisibleWhen] — an empty
        /// GroupBox with just a header is worse than no section. Rows without the attribute are
        /// always visible, so a section holding one is never gated at all. A binding rather than
        /// a PropertyChanged subscription, for the same reason the rows use one: the settings
        /// objects outlive every settings window, and Avalonia's bindings subscribe weakly.
        /// </summary>
        private static void GateGroupVisibility(Control box, List<(SettingsControlFactory Owner, PropertyDescriptor Pd)> group)
        {
            var gates = new List<(object Source, VisibleWhenAttribute Attr)>();
            foreach (var (owner, pd) in group)
            {
                var attr = GetFirstAttributeOrDefault<VisibleWhenAttribute>(pd);
                var source = attr == null ? null : ResolveGateSource(owner._obj, attr);
                if (source == null)
                    return;
                gates.Add((source, attr));
            }

            var binding = new MultiBinding
            {
                Mode = BindingMode.OneWay,
                // values arrive in gate order, so pair each back with the attribute it answers.
                Converter = new FuncMultiValueConverter<object, bool>(
                    values => values.Select((v, i) => gates[i].Attr.Matches(v)).Any(shown => shown)),
            };
            foreach (var (source, attr) in gates)
                binding.Bindings.Add(new Binding(attr.PropertyName) { Source = source, Mode = BindingMode.OneWay });

            box.Bind(Visual.IsVisibleProperty, binding);
        }

        /// <summary>The object a [VisibleWhen] reads its gating property from: the row's own
        /// settings object, or the named <see cref="SettingsRoot"/> section for a row that follows
        /// another page's choice. Null (no root yet, or an unknown section) leaves the row shown.</summary>
        private static object ResolveGateSource(object own, VisibleWhenAttribute attr)
        {
            if (String.IsNullOrEmpty(attr.Section))
                return own;

            var root = SettingsRoot.Current;
            return root == null ? null : TypeDescriptor.GetProperties(root)[attr.Section]?.GetValue(root);
        }

        /// <summary>
        /// The tile selector for <paramref name="propertyName"/> of <paramref name="obj"/> (a
        /// [ModeSelector] enum property), for hand-written pages that host one above their own
        /// content — the Uploads page. Same control, same titles and captions as the generated
        /// pages get, so the enum stays the one place those words live.
        /// </summary>
        public static Control CreateModeSelector(object obj, string propertyName)
        {
            var pd = TypeDescriptor.GetProperties(obj)[propertyName]
                ?? throw new ArgumentException($"{obj.GetType().Name} has no property {propertyName}.", nameof(propertyName));
            return new SettingsControlFactory(() => null, obj).BuildModeSelector(pd);
        }

        private void AddRow(SettingsRowsPanel panel, PropertyDescriptor pd)
        {
            var description = GetFirstAttributeOrDefault<DescriptionAttribute>(pd)?.Description;
            var editor = GetRowForProperty(pd);

            // [DisabledWhen] rows stay visible but stop responding (and dim) while the bool they
            // defer to holds the disabling value — e.g. the manual accent color while the OS
            // accent is followed, or the scroll rewind while scrolling capture is switched off.
            // The whole row dims, label and caption included: a grayed control beside a bright
            // label reads as a broken control rather than as a switched-off option, and a run of
            // gated rows (the composition section) makes that especially obvious.
            var disabledWhen = GetFirstAttributeOrDefault<DisabledWhenAttribute>(pd);

            // enabledOpacity is what the target would have shown anyway — the caption is already
            // dimmed to 0.65, and binding Opacity replaces that local value rather than scaling it.
            void ApplyDisabledWhen(Control target, double enabledOpacity = 1.0)
            {
                if (disabledWhen == null)
                    return;

                var disablingValue = disabledWhen.DisablingValue;
                target.Bind(InputElement.IsEnabledProperty, new Binding(disabledWhen.PropertyName)
                {
                    Source = _obj,
                    Mode = BindingMode.OneWay,
                    Converter = new FuncValueConverter<bool, bool>(v => v != disablingValue),
                });
                target.Bind(Visual.OpacityProperty, new Binding(disabledWhen.PropertyName)
                {
                    Source = _obj,
                    Mode = BindingMode.OneWay,
                    Converter = new FuncValueConverter<bool, double>(
                        v => v == disablingValue ? enabledOpacity * 0.4 : enabledOpacity),
                });
            }

            // [VisibleWhen] rows leave the page altogether — label, editor and caption — while the
            // property they defer to holds a value they were not declared for; the section goes
            // with them once its last row is gone (see GateGroupVisibility).
            var visibleWhen = GetFirstAttributeOrDefault<VisibleWhenAttribute>(pd);

            void ApplyVisibleWhen(Control target)
            {
                var source = visibleWhen == null ? null : ResolveGateSource(_obj, visibleWhen);
                if (source == null)
                    return;

                target.Bind(Visual.IsVisibleProperty, new Binding(visibleWhen.PropertyName)
                {
                    Source = source,
                    Mode = BindingMode.OneWay,
                    Converter = new FuncValueConverter<object, bool>(visibleWhen.Matches),
                });
            }

            // the caption hugs its own row (2px); rows without one carry the full bottom gap
            // themselves so vertical rhythm stays even either way (12+12 = ~24px between settings).
            var bottom = String.IsNullOrEmpty(description) ? 12d : 2d;

            var rowLabel = new TextBlock();
            rowLabel.VerticalAlignment = VerticalAlignment.Center;
            rowLabel.HorizontalAlignment = HorizontalAlignment.Left;
            // wraps when the panel narrows the label column (a long title in a small window)
            rowLabel.TextWrapping = TextWrapping.Wrap;
            rowLabel.Margin = new Thickness(0, 12, 0, bottom);
            rowLabel.Text = FromCamelCase(pd.DisplayName);
            SettingsRowsPanel.SetRole(rowLabel, SettingsRowsPanel.Role.Label);

            var rowContent = new Border();
            rowContent.VerticalAlignment = VerticalAlignment.Center;
            // left-aligned with a width cap so rows read as label→control pairs instead
            // of full-width bands (editors carry their own preferred width).
            rowContent.HorizontalAlignment = HorizontalAlignment.Left;
            rowContent.MaxWidth = 460;
            rowContent.Child = editor;
            rowContent.Margin = new Thickness(24, 12, 4, bottom);
            SettingsRowsPanel.SetRole(rowContent, SettingsRowsPanel.Role.Editor);

            panel.Children.Add(rowLabel);
            panel.Children.Add(rowContent);

            // the Border carries the gate for the editor inside it, so IsEnabled cascades.
            ApplyDisabledWhen(rowLabel);
            ApplyDisabledWhen(rowContent);
            ApplyVisibleWhen(rowLabel);
            ApplyVisibleWhen(rowContent);

            if (!String.IsNullOrEmpty(description))
            {
                var caption = new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.65,
                    MaxWidth = 560,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 4, 12),
                };
                SettingsRowsPanel.SetRole(caption, SettingsRowsPanel.Role.Caption);
                panel.Children.Add(caption);
                ApplyDisabledWhen(caption, enabledOpacity: 0.65);
                ApplyVisibleWhen(caption);
            }
        }

        private Control GetRowForProperty(PropertyDescriptor pd)
        {
            var type = pd.PropertyType;
            var tcode = Type.GetTypeCode(pd.PropertyType);

            // audio device ids render as a dropdown of live devices; the property stays a plain
            // string ("default" or the platform device id passed through to obs-express).
            var audioSelector = GetFirstAttributeOrDefault<AudioDeviceSelectorAttribute>(pd);
            if (audioSelector != null && Is(pd, typeof(string)))
                return AudioDeviceComboBinding(audioSelector.DeviceType, pd);

            // …and the same for camera ids, which the recorder enumerates for us.
            if (GetFirstAttributeOrDefault<CameraDeviceSelectorAttribute>(pd) != null && Is(pd, typeof(string)))
                return CameraDeviceComboBinding(pd);

            if (Is(pd, typeof(string)))
            {
                // a property with a known-but-open value set (e.g. AWS region) renders as an
                // editable dropdown: pick a suggestion or type your own.
                var suggested = GetFirstAttributeOrDefault<SuggestedValuesAttribute>(pd);
                if (suggested != null)
                    return SuggestedValuesBinding(suggested, pd);

                // a preferred rather than minimum width: in a narrow window the box shrinks
                // instead of running under the Browse button / off the page (PreferredWidthBox).
                var txt = new PreferredWidthBox(280, SimpleControlBinding(new TextBox(), pd, TextBox.TextProperty));
                if (pd.Name.EndsWith("Directory"))
                {
                    var btn = ButtonControl("Browse", "Secondary", async (s, e) =>
                    {
                        var storage = _wndFn()?.StorageProvider;
                        if (storage == null)
                            return;

                        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                        {
                            Title = "Pick a folder",
                            AllowMultiple = false,
                        });

                        var path = picked?.FirstOrDefault()?.TryGetLocalPath();
                        if (!String.IsNullOrEmpty(path))
                            pd.SetValue(_obj, path);
                    });
                    btn.Margin = new Thickness(10, 0, 0, 0);
                    btn.VerticalAlignment = VerticalAlignment.Center;
                    return DockCtrl(txt, btn, Dock.Right);
                }

                if (pd.Name == nameof(SettingsCapture.FilenamePattern))
                {
                    // captures are saved as png, recordings as mp4 (SettingsRecording carries the
                    // same-named pattern property, issue #50).
                    var extension = _obj is SettingsRecording ? "mp4" : "png";

                    // live example makes the date-format string self-documenting.
                    var preview = new TextBlock
                    {
                        FontSize = 12,
                        Opacity = 0.65,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(2, 4, 0, 0),
                    };
                    preview.Bind(TextBlock.TextProperty, new Binding(pd.Name)
                    {
                        Source = _obj,
                        Mode = BindingMode.OneWay,
                        Converter = new FuncValueConverter<string, string>(p => FormatFilenamePreview(p, extension)),
                    });

                    var panel = new StackPanel { Orientation = Orientation.Vertical };
                    panel.Children.Add(txt);
                    panel.Children.Add(preview);
                    return panel;
                }

                return txt;
            }

            if (Is(pd, typeof(bool)))
            {
                // the visible label lives in the row's label column (consistent with every other
                // editor); the checkbox itself still carries the name for screen readers.
                var checkbox = new CheckBox();
                Avalonia.Automation.AutomationProperties.SetName(checkbox, FromCamelCase(pd.DisplayName));
                return SimpleControlBinding(checkbox, pd, CheckBox.IsCheckedProperty);
            }

            if (pd.PropertyType.IsEnum)
                return ComboSelectBinding(() => Enum.GetValues(type), pd, GetEnumDisplayString, false);

            if ((int)tcode >= (int)TypeCode.Char && (int)tcode <= (int)TypeCode.Decimal)
            {
                var range = GetFirstAttributeOrDefault<System.ComponentModel.DataAnnotations.RangeAttribute>(pd);

                // a 0..1 fraction reads much better as a percentage slider than a spinner.
                if (range != null && Convert.ToDouble(range.Minimum) == 0.0 && Convert.ToDouble(range.Maximum) == 1.0)
                {
                    var slider = new Slider
                    {
                        Minimum = 0,
                        Maximum = 1,
                        TickFrequency = 0.05,
                        IsSnapToTickEnabled = true,
                        Width = 200,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    slider.Bind(Slider.ValueProperty, CreateBinding(pd.Name));

                    var valueLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
                    valueLabel.Bind(TextBlock.TextProperty, new Binding(pd.Name)
                    {
                        Source = _obj,
                        Mode = BindingMode.OneWay,
                        Converter = new FuncValueConverter<double, string>(d => d.ToString("P0")),
                    });

                    return StackCtrl(slider, valueLabel);
                }

                // decision table #53: WPFUI NumberBox -> NumericUpDown (decimal?) bridged to the
                // int/double settings property by NumericTypeConverter. [Range] bounds the spinner
                // so it cannot walk the value out of the valid domain.
                var numeric = new NumericUpDown { Increment = 1, FormatString = "0.##" };
                if (range != null)
                {
                    numeric.Minimum = Convert.ToDecimal(range.Minimum);
                    numeric.Maximum = Convert.ToDecimal(range.Maximum);
                    if (numeric.Maximum - numeric.Minimum <= 2)
                        numeric.Increment = 0.05m;
                }

                return new PreferredWidthBox(160, SimpleControlBinding(numeric, pd, NumericUpDown.ValueProperty, new NumericTypeConverter()));
            }

            if (Is(pd, typeof(Color)))
            {
                var inner = new Border
                {
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                };
                inner.Bind(Border.BackgroundProperty, CreateBinding(pd.Name, new ColorToBrushConverter()));

                var border = new Border
                {
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Child = inner,
                    Background = AppStyles.CheckerboardBrushSmall,
                    Width = 24,
                    Height = 24,
                };

                var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                label.Bind(TextBlock.TextProperty, CreateBinding(pd.Name, new ColorToHexConverter()));

                var stack = StackCtrl(border, label);
                stack.Cursor = new Cursor(StandardCursorType.Hand);
                stack.PointerPressed += async (s, e) =>
                {
                    var result = await NiceDialog.ShowColorPromptAsync(_wndFn(), (Color)pd.GetValue(_obj));
                    pd.SetValue(_obj, result);
                };
                return stack;
            }

            if (Is(pd, typeof(SimpleKeyGesture)))
                return new GlobalTriggerEditor { Entry = HotkeyManager.Current?.GetEntryForProperty(pd.Name) };

            if (Is(pd, typeof(Control)))
            {
                return (Control)pd.GetValue(_obj);
            }

            if (Is(pd, typeof(Dictionary<,>)))
            {
                return ButtonControl("Reset all tool defaults…", "Danger", async (s, e) =>
                {
                    if (await NiceDialog.ShowYesNoPromptAsync(s as Visual, NiceDialogIcon.Warning,
                            "Reset every tool's saved color, line width and font back to the defaults? This cannot be undone."))
                    {
                        pd.SetValue(_obj, Activator.CreateInstance(pd.PropertyType));
                    }
                });
            }

            if (Is(pd, typeof(TimeOption)))
            {
                var child = new SettingsControlFactory(_wndFn, pd.GetValue(_obj));
                var pdNum = pd.GetChildProperties().OfType<PropertyDescriptor>().FirstOrDefault(t => t.Name == nameof(TimeOption.Number));
                var pdUnit = pd.GetChildProperties().OfType<PropertyDescriptor>().FirstOrDefault(t => t.Name == nameof(TimeOption.Unit));
                var ctNum = child.SimpleControlBinding(new NumericUpDown { MinWidth = 120, Minimum = 1, Increment = 1, FormatString = "0" },
                                                       pdNum, NumericUpDown.ValueProperty, new NumericTypeConverter());
                var ctUnit = child.ComboSelectBinding(() => Enum.GetValues(pdUnit.PropertyType), pdUnit, null, false);
                return StackCtrl(ctNum, ctUnit);
            }

            return new TextBlock { Text = pd.Name, VerticalAlignment = VerticalAlignment.Center };
        }

        /// <summary>
        /// The tile row for a [ModeSelector] enum property: one <see cref="ModeOption"/> per
        /// member, titled by its [Description] (the same text the dropdown editor would show) and
        /// captioned by its [ModeCaption].
        /// </summary>
        private Control BuildModeSelector(PropertyDescriptor pd)
        {
            if (!pd.PropertyType.IsEnum)
                throw new InvalidOperationException($"[ModeSelector] on {pd.ComponentType.Name}.{pd.Name} needs an enum property.");

            var selector = new ModeSelector
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 720,
            };

            foreach (var value in Enum.GetValues(pd.PropertyType))
            {
                var caption = pd.PropertyType.GetField(value.ToString())
                    ?.GetCustomAttributes(typeof(ModeCaptionAttribute), false)
                    .OfType<ModeCaptionAttribute>().FirstOrDefault()?.Caption;

                selector.Options.Add(new ModeOption
                {
                    Value = value,
                    Title = GetEnumDisplayString(value),
                    Caption = caption,
                });
            }

            // bound after the options are all in, so the stored value is never seen against a
            // partial set (the selector's own fallback is deferred to attachment for the same
            // reason — this is the belt to that suspenders).
            selector.Bind(ModeSelector.SelectedValueProperty, CreateBinding(pd.Name));
            return selector;
        }

        public static string FromCamelCase(string variableName)
        {
            if (variableName.Contains(' '))
                return variableName;
            var sb = new StringBuilder();
            for (int i = 0; i < variableName.Length; i++)
            {
                if (i > 0 && char.IsUpper(variableName[i]) && !char.IsUpper(variableName[i - 1]))
                {
                    sb.Append(" ");
                    if (i == variableName.Length - 1 || char.IsUpper(variableName[i + 1]))
                    {
                        sb.Append(variableName[i]);
                    }
                    else
                    {
                        sb.Append(variableName[i].ToString(CultureInfo.InvariantCulture).ToLower());
                    }

                    continue;
                }

                sb.Append(variableName[i]);
            }

            return sb.ToString();
        }

        /// <summary>Renders an example output filename for a date-format pattern (mirrors
        /// UploadManager.GetPatternFileName / NiceDialog.ShowSaveImageDialog).</summary>
        private static string FormatFilenamePreview(string pattern, string extension)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(pattern))
                    pattern = SettingsCapture.DefaultFilenamePattern;

                var name = DateTime.Now.ToString(System.IO.Path.GetFileNameWithoutExtension(pattern));
                if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                    return "⚠ The pattern produces characters that are not allowed in file names.";

                return $"Example: {name}.{extension}";
            }
            catch
            {
                return "⚠ Invalid .NET date format pattern.";
            }
        }

        private static bool Is(PropertyDescriptor pd, Type type)
        {
            if (type.IsGenericTypeDefinition)
                return pd.PropertyType.IsGenericType && pd.PropertyType.GetGenericTypeDefinition() == type;

            return type.IsAssignableFrom(pd.PropertyType);
        }

        private static T GetFirstAttributeOrDefault<T>(PropertyDescriptor pd) where T : Attribute
        {
            return pd.Attributes.OfType<T>().FirstOrDefault();
        }

        public static string GetEnumDisplayString(object arg)
        {
            if (arg == null)
                return null;

            try
            {
                var valueAttributes = arg.GetType().GetField(arg.ToString())?.GetCustomAttributes(false);
                var desc = valueAttributes?.FirstOrDefault(a => a is DescriptionAttribute) as DescriptionAttribute;
                if (desc != null)
                {
                    return desc.Description;
                }
            }
            catch {; }

            return arg.ToString();
        }

        Binding CreateBinding(string bindingPath, IValueConverter converter = null)
        {
            return new Binding(bindingPath)
            {
                Mode = BindingMode.TwoWay,
                Converter = converter,
                Source = _obj,
            };
        }

        Panel StackCtrl(params Control[] children)
        {
            var panel = new StackPanel();
            panel.Spacing = 10;
            panel.Orientation = Orientation.Horizontal;
            panel.VerticalAlignment = VerticalAlignment.Stretch;
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Background = Brushes.Transparent; // hit-testable for PointerPressed
            foreach (var c in children)
                panel.Children.Add(c);

            return panel;
        }

        Control DockCtrl(Control fill, Control dock, Dock position)
        {
            var panel = new DockPanel();
            panel.VerticalAlignment = VerticalAlignment.Stretch;
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;

            DockPanel.SetDock(dock, position);
            panel.Children.Add(dock);
            panel.Children.Add(fill);
            return panel;
        }

        Control SimpleControlBinding(Control control, PropertyDescriptor pd, AvaloniaProperty property, IValueConverter converter = null)
        {
            control.Bind(property, CreateBinding(pd.Name, converter));
            return control;
        }

        /// <summary>A dropdown of live audio devices for a string device-id property. The list is
        /// rebuilt on every open (device hot-plug); a stored id that no longer exists is kept as a
        /// raw-id row so the user can see the selection is stale rather than it silently changing.</summary>
        Control AudioDeviceComboBinding(string deviceType, PropertyDescriptor pd)
        {
            var isSpeaker = String.Equals(deviceType, AudioDeviceManager.TypeSpeaker, StringComparison.OrdinalIgnoreCase);

            List<AudioDeviceInfo> BuildItems()
            {
                var items = isSpeaker ? AudioDeviceManager.GetSpeakers() : AudioDeviceManager.GetMicrophones();
                var current = pd.GetValue(_obj) as string;
                if (!String.IsNullOrEmpty(current) && !items.Any(d => d.DeviceId == current))
                    items.Add(new AudioDeviceInfo(current, deviceType, current));
                return items;
            }

            // Semi aligns ComboBox Left, so it must be told to fill the preferred-width box.
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.ItemTemplate = new FuncDataTemplate<AudioDeviceInfo>((o, ns) => ComboItemText(o?.FriendlyName));

            void Reload()
            {
                var items = BuildItems();
                var current = pd.GetValue(_obj) as string;
                combo.ItemsSource = items;
                combo.SelectedItem = items.FirstOrDefault(d => d.DeviceId == current) ?? items.FirstOrDefault();
            }

            Reload();
            combo.DropDownOpened += (s, e) => Reload();
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is AudioDeviceInfo info && (pd.GetValue(_obj) as string) != info.DeviceId)
                    pd.SetValue(_obj, info.DeviceId);
            };

            return new PreferredWidthBox(280, combo);
        }

        /// <summary>A dropdown of cameras for a string device-id property, the camera counterpart of
        /// <see cref="AudioDeviceComboBinding"/>. Two differences, both forced by the enumeration
        /// costing a process spawn rather than a COM call: the list is filled asynchronously (the
        /// combo shows the stored device until it lands, so building the settings page never blocks
        /// on a camera driver), and it is not rebuilt every time the dropdown opens — the trailing
        /// refresh row is how a hot-plugged camera is picked up. A leading "(none)" row keeps
        /// "no camera" expressible, which audio does not need (it always has a default device).</summary>
        Control CameraDeviceComboBinding(PropertyDescriptor pd)
        {
            const string NoneId = "";
            const string RefreshId = "__clowd_refresh__"; // not a device id any platform can produce

            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.ItemTemplate = new FuncDataTemplate<CameraDeviceInfo>((o, ns) => ComboItemText(o?.FriendlyName));

            List<CameraDeviceInfo> BuildItems(List<CameraDeviceInfo> cameras)
            {
                var items = new List<CameraDeviceInfo> { new CameraDeviceInfo(NoneId, "(none)") };
                items.AddRange(cameras);

                var current = pd.GetValue(_obj) as string;
                if (!String.IsNullOrEmpty(current) && !items.Any(d => d.DeviceId == current))
                    items.Add(new CameraDeviceInfo(current, current));

                items.Add(new CameraDeviceInfo(RefreshId, "Refresh camera list…"));
                return items;
            }

            void Apply(List<CameraDeviceInfo> cameras)
            {
                var items = BuildItems(cameras);
                var current = pd.GetValue(_obj) as string ?? NoneId;
                combo.ItemsSource = items;
                combo.SelectedItem = items.FirstOrDefault(d => d.DeviceId == current) ?? items[0];
            }

            // show the stored value immediately; the real list replaces it when it arrives.
            Apply(new List<CameraDeviceInfo>());
            _ = FillAsync(CameraDeviceManager.GetCamerasAsync());

            async System.Threading.Tasks.Task FillAsync(System.Threading.Tasks.Task<List<CameraDeviceInfo>> pending)
            {
                try
                {
                    var cameras = await pending;
                    // the settings page may already be gone; assigning to a detached combo is harmless.
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Apply(cameras));
                }
                catch (Exception ex)
                {
                    // CameraDeviceManager never throws, so this is a dispatcher failure only.
                    System.Diagnostics.Debug.WriteLine("Failed to fill the camera list: " + ex);
                }
            }

            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is not CameraDeviceInfo info)
                    return;

                if (info.DeviceId == RefreshId)
                {
                    // the refresh row is an action, not a value: put the selection back on the
                    // stored device and re-enumerate underneath it.
                    var stored = pd.GetValue(_obj) as string ?? NoneId;
                    combo.SelectedItem = (combo.ItemsSource as List<CameraDeviceInfo>)?.FirstOrDefault(d => d.DeviceId == stored);
                    _ = FillAsync(CameraDeviceManager.RefreshAsync());
                    return;
                }

                if ((pd.GetValue(_obj) as string ?? NoneId) != info.DeviceId)
                    pd.SetValue(_obj, info.DeviceId);
            };

            return new PreferredWidthBox(280, combo);
        }

        /// <summary>An editable field for a string property carrying [SuggestedValues]: a free-text
        /// box (so the user can type a value of their own, e.g. a custom region string for a
        /// third-party S3 endpoint) with a chevron button hosted *inside* the box (via
        /// InnerRightContent) that drops down the known values. Deliberately NOT an AutoCompleteBox —
        /// that control throws on detach in Avalonia 11.3; a Flyout-hosted ListBox uses the same
        /// stable popup path as the ComboBox editors.</summary>
        Control SuggestedValuesBinding(SuggestedValuesAttribute attr, PropertyDescriptor pd)
        {
            var txt = new TextBox { VerticalAlignment = VerticalAlignment.Center };
            txt.Bind(TextBox.TextProperty, CreateBinding(pd.Name));

            var list = new ListBox
            {
                ItemsSource = attr.GetValues(),
                MinWidth = 180,
                MaxHeight = 300, // ListBox scrolls internally past this
            };

            var flyout = new Flyout { Content = list };

            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is string picked)
                {
                    pd.SetValue(_obj, picked);
                    flyout.Hide();
                    list.SelectedItem = null; // reset so picking the same value again still registers
                }
            };

            // a flat, chrome-less chevron that lives inside the text box's right edge
            var btn = new Button
            {
                Content = "▾",
                Flyout = flyout,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };

            txt.InnerRightContent = btn;
            return new PreferredWidthBox(280, txt);
        }

        /// <summary>The text of a dropdown row (and of the selection box, which reuses the item
        /// template). Trimmed rather than wrapped: a squeezed combo - a long device name in a
        /// narrow window - ellipsizes, and the dropdown still shows the full names.</summary>
        static TextBlock ComboItemText(string text)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
        }

        Control ComboSelectBinding(Func<IEnumerable> items, PropertyDescriptor pd, Func<object, string> display = null, bool canClear = true)
        {
            display ??= o => o?.ToString();

            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.ItemTemplate = new FuncDataTemplate<object>((o, ns) => ComboItemText(display(o)));
            combo.ItemsSource = items();
            combo.DropDownOpened += (s, e) => { combo.ItemsSource = items(); };
            combo.Bind(ComboBox.SelectedItemProperty, CreateBinding(pd.Name));

            if (canClear)
            {
                var panel = new DockPanel();
                var reset = new Button();
                reset.Classes.Add("Tertiary"); // Semi tertiary button for the low-emphasis clear action
                DockPanel.SetDock(reset, Dock.Right);
                reset.Margin = new Thickness(5, 0, 0, 0);
                reset.Padding = new Thickness(10, 0, 10, 0);
                reset.Content = "Clear";
                reset.Click += (s, e) => { combo.SelectedIndex = -1; };
                panel.Children.Add(reset);
                panel.Children.Add(new PreferredWidthBox(160, combo));
                return panel;
            }
            else
            {
                return new PreferredWidthBox(160, combo);
            }
        }

        Control ButtonControl(string buttonText, string buttonClass, EventHandler<RoutedEventArgs> buttonClick, Control top = null,
            Control right = null, Control bottom = null)
        {
            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Vertical;

            var btn = new Button();
            if (!String.IsNullOrEmpty(buttonClass))
                btn.Classes.Add(buttonClass); // Semi style class (Secondary/Tertiary/Danger/...)
            btn.Margin = new Thickness(0, top == null ? 0 : 5, right == null ? 0 : 5, bottom == null ? 0 : 5);
            btn.Content = buttonText;
            btn.Click += buttonClick;

            if (top != null)
                panel.Children.Add(top);

            if (right == null)
            {
                panel.Children.Add(btn);
            }
            else
            {
                StackPanel panel2 = new StackPanel();
                panel2.Orientation = Orientation.Horizontal;
                panel2.Children.Add(btn);
                panel2.Children.Add(right);
                panel.Children.Add(panel2);
            }

            if (bottom != null)
                panel.Children.Add(bottom);

            return panel;
        }
    }
}
