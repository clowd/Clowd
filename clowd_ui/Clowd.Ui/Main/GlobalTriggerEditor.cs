using System;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Clowd.UI.Config
{
    /// <summary>
    /// Gesture editor row for a <see cref="HotkeyEntry"/>: a gesture button (click, then press
    /// the new combination), a clear button, and a live status indicator (colored dot + text,
    /// with the registration error in a tooltip). Cancelling an edit (Esc / focus loss) restores
    /// the previous gesture.
    /// </summary>
    public class GlobalTriggerEditor : UserControl
    {
        public static readonly StyledProperty<HotkeyEntry> EntryProperty =
            AvaloniaProperty.Register<GlobalTriggerEditor, HotkeyEntry>(nameof(Entry));

        public HotkeyEntry Entry
        {
            get => GetValue(EntryProperty);
            set => SetValue(EntryProperty, value);
        }

        public bool IsEditing { get; private set; }

        private readonly Button _button;
        private readonly Button _clearButton;
        private readonly Ellipse _statusDot;
        private readonly TextBlock _statusText;
        private readonly StackPanel _statusPanel;
        private KeyModifiers _editModifiers;
        private SimpleKeyGesture _gestureBeforeEdit;

        public GlobalTriggerEditor()
        {
            var grid = new Grid { MinWidth = 340 };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(6)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            _button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            _button.Click += OnButtonClick;
            ToolTip.SetTip(_button, "Click, then press the new key combination (Esc cancels)");
            grid.Children.Add(_button);

            _clearButton = new Button
            {
                Content = "✕",
                Width = 28,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            _clearButton.Classes.Add("Tertiary");
            _clearButton.Click += OnClearClick;
            ToolTip.SetTip(_clearButton, "Remove this shortcut");
            AutomationProperties.SetName(_clearButton, "Remove shortcut");
            Grid.SetColumn(_clearButton, 2);
            grid.Children.Add(_clearButton);

            _statusDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _statusText = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _statusPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                // wide enough for the longest status ("Not registered") so every row's grid —
                // and therefore every gesture button — ends up the same width.
                MinWidth = 110,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent, // hit-testable so the error tooltip shows
            };
            _statusPanel.Children.Add(_statusDot);
            _statusPanel.Children.Add(_statusText);
            Grid.SetColumn(_statusPanel, 4);
            grid.Children.Add(_statusPanel);

            Content = grid;
            Focusable = true;

            ActualThemeVariantChanged += (_, _) => UpdateControls();

            UpdateControls();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == EntryProperty)
            {
                if (change.OldValue is HotkeyEntry oldEntry)
                    oldEntry.PropertyChanged -= OnEntryPropertyChanged;
                if (change.NewValue is HotkeyEntry newEntry)
                    newEntry.PropertyChanged += OnEntryPropertyChanged;
                UpdateControls();
            }
        }

        private void OnEntryPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // IsRegistered / Error / Gesture can all change asynchronously (e.g. the SharpHook
            // host failing to start on macOS without the Accessibility permission) — keep the
            // status indicator and gesture text live.
            UpdateControls();
        }

        private void OnButtonClick(object sender, RoutedEventArgs e)
        {
            var manager = HotkeyManager.Current;
            if (IsEditing || Entry == null || manager == null || manager.IsPaused)
                return;
            manager.IsPaused = true;
            IsEditing = true;
            _editModifiers = KeyModifiers.None;
            // unregister while listening so the previous binding can be re-used; restored on cancel.
            _gestureBeforeEdit = Entry.Gesture;
            Entry.Gesture = null;
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            LostFocus += OnLostFocus;
            Focus();
            UpdateControls();
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            if (IsEditing || Entry == null)
                return;

            Entry.Gesture = null;
            UpdateControls();
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            // the button's own focus loss (when Focus() moves focus to this control) bubbles
            // here too — only react when this control itself loses focus.
            if (!ReferenceEquals(e.Source, this))
                return;

            FinishEditing(Key.None, KeyModifiers.None);
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            // PrtScr does not arrive in KeyDown on Windows — only in KeyUp.
            _editModifiers = e.KeyModifiers;
            if (e.Key == Key.PrintScreen)
            {
                e.Handled = true;
                FinishEditing(e.Key, e.KeyModifiers);
            }
            else
            {
                UpdateControls();
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            _editModifiers = e.KeyModifiers;
            var keyCode = (int)e.Key;
            // ignore any known modifier keys (LWin/RWin = 70/71, LeftShift..RightAlt = 116..121).
            if (keyCode == 70 || keyCode == 71 || (keyCode >= 116 && keyCode <= 121))
            {
                UpdateControls();
            }
            else
            {
                e.Handled = true;
                FinishEditing(e.Key, e.KeyModifiers);
            }
        }

        private void FinishEditing(Key key, KeyModifiers modifiers)
        {
            IsEditing = false;
            if (HotkeyManager.Current != null)
                HotkeyManager.Current.IsPaused = false;
            KeyDown -= OnKeyDown;
            KeyUp -= OnKeyUp;
            LostFocus -= OnLostFocus;

            if (Entry != null)
            {
                if (!IsBlacklisted(key, modifiers))
                {
                    try
                    {
                        Entry.Gesture = new SimpleKeyGesture(key, modifiers);
                    }
                    catch
                    {
                        // invalid keygesture — treat as a cancelled edit
                        Entry.Gesture = _gestureBeforeEdit;
                    }
                }
                else
                {
                    // cancelled (Esc / focus loss) — an aborted edit must not destroy the binding.
                    Entry.Gesture = _gestureBeforeEdit;
                }
            }

            _gestureBeforeEdit = null;
            UpdateControls();
        }

        private bool IsBlacklisted(Key key, KeyModifiers modifiers)
        {
            if (key == Key.None)
                return true;

            if (key == Key.Escape && modifiers == KeyModifiers.None)
                return true;

            return false;
        }

        private IBrush GetToken(string key, IBrush fallback)
        {
            if (this.TryFindResource(key, ActualThemeVariant, out var value))
            {
                if (value is IBrush brush)
                    return brush;
                if (value is Color color)
                    return new SolidColorBrush(color);
            }

            return fallback;
        }

        private void SetStatus(string tokenKey, IBrush fallback, string text, string tooltip)
        {
            _statusDot.Fill = GetToken(tokenKey, fallback);
            _statusText.Text = text;
            ToolTip.SetTip(_statusPanel, tooltip);
        }

        /// <summary>The clear button always reserves its 28px column; when it is not applicable
        /// the gesture button spans across it, so "(not set)" rows end at exactly the same right
        /// edge as [gesture][✕] rows.</summary>
        private void SetClearVisible(bool visible)
        {
            _clearButton.Opacity = visible ? 1 : 0;
            _clearButton.IsHitTestVisible = visible;
            _clearButton.Focusable = visible;
            Grid.SetColumnSpan(_button, visible ? 1 : 3);
        }

        private void UpdateControls()
        {
            if (_button == null || _statusDot == null)
                return;

            if (IsEditing)
            {
                SetStatus("SemiColorWarning", Brushes.Orange, "Listening…", null);
                SetClearVisible(false);

                StringBuilder key = new StringBuilder();
                if (_editModifiers.HasFlag(KeyModifiers.Control))
                    key.Append("Ctrl+");
                if (_editModifiers.HasFlag(KeyModifiers.Alt))
                    key.Append("Alt+");
                if (_editModifiers.HasFlag(KeyModifiers.Shift))
                    key.Append("Shift+");
                if (_editModifiers.HasFlag(KeyModifiers.Meta))
                    key.Append("Meta+");

                key.Append(" ...");
                _button.Content = key.ToString();
                return;
            }

            if (Entry == null || Entry.Gesture == null)
            {
                _button.Content = "(not set)";
                SetClearVisible(false);
                SetStatus("SemiColorText2", Brushes.Gray, "Not set",
                          Entry?.Error ?? "No key combination is assigned to this action.");
            }
            else
            {
                // the gesture text is kept visible even on error — the red dot + tooltip carry
                // the registration failure.
                _button.Content = Entry.Gesture.ToString();
                SetClearVisible(true);
                if (!Entry.IsRegistered && !String.IsNullOrEmpty(Entry.Error))
                {
                    SetStatus("SemiColorDanger", Brushes.IndianRed, "Not registered", Entry.Error);
                }
                else
                {
                    SetStatus("SemiColorSuccess", Brushes.Green, "Active", null);
                }
            }
        }
    }
}
