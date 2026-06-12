using System;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Clowd.Config;

namespace Clowd.UI.Config
{
    /// <summary>
    /// Gesture editor row for a <see cref="GlobalTrigger"/>. The gesture can be viewed and edited
    /// (and is persisted); the status square reflects the trigger's live registration state
    /// (green when the SharpHook host registered it, red with the error in a tooltip otherwise).
    /// </summary>
    public class GlobalTriggerEditor : UserControl
    {
        public static readonly StyledProperty<GlobalTrigger> TriggerProperty =
            AvaloniaProperty.Register<GlobalTriggerEditor, GlobalTrigger>(nameof(Trigger));

        public GlobalTrigger Trigger
        {
            get => GetValue(TriggerProperty);
            set => SetValue(TriggerProperty, value);
        }

        public bool IsEditing { get; private set; }

        private readonly Button _button;
        private readonly Border _status;
        private KeyModifiers _editModifiers;

        public GlobalTriggerEditor()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(50)));

            _button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            _button.Click += OnButtonClick;
            ToolTip.SetTip(_button, "Click to edit the current gesture");
            grid.Children.Add(_button);

            _status = new Border
            {
                BorderBrush = Brushes.DarkGray,
                BorderThickness = new Thickness(1),
            };
            Grid.SetColumn(_status, 2);
            grid.Children.Add(_status);

            Content = grid;
            Focusable = true;

            UpdateControls();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == TriggerProperty)
            {
                if (change.OldValue is GlobalTrigger oldTrigger)
                    oldTrigger.PropertyChanged -= OnTriggerPropertyChanged;
                if (change.NewValue is GlobalTrigger newTrigger)
                    newTrigger.PropertyChanged += OnTriggerPropertyChanged;
                UpdateControls();
            }
        }

        private void OnTriggerPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // IsRegistered / Error / KeyGesture can all change asynchronously (e.g. the SharpHook
            // host failing to start on macOS without the Accessibility permission) — keep the
            // status square and gesture text live.
            UpdateControls();
        }

        private void OnButtonClick(object sender, RoutedEventArgs e)
        {
            if (IsEditing || GlobalTrigger.IsPaused || Trigger == null)
                return;
            GlobalTrigger.IsPaused = true;
            IsEditing = true;
            _editModifiers = KeyModifiers.None;
            Trigger.KeyGesture = null;
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            LostFocus += OnLostFocus;
            Focus();
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
            GlobalTrigger.IsPaused = false;
            KeyDown -= OnKeyDown;
            KeyUp -= OnKeyUp;
            LostFocus -= OnLostFocus;

            if (!IsBlacklisted(key, modifiers) && Trigger != null)
            {
                try
                {
                    Trigger.KeyGesture = new SimpleKeyGesture(key, modifiers);
                }
                catch
                {
                    // invalid keygesture
                }
            }

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

        private void UpdateControls()
        {
            if (_button == null || _status == null)
                return;

            if (IsEditing)
            {
                _status.Background = Brushes.PaleGoldenrod;
                ToolTip.SetTip(_status, null);

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
            }
            else
            {
                if (Trigger == null || Trigger.KeyGesture == null)
                {
                    _button.Content = "(not set)";
                    ToolTip.SetTip(_status, Trigger?.Error ?? "The gesture is not set or is an invalid gesture.");
                    _status.Background = Brushes.PaleVioletRed;
                }
                else
                {
                    // unlike WPF (which displayed "(error)"), the gesture text is kept visible —
                    // the red status square + tooltip carry the registration error.
                    _button.Content = Trigger.KeyGesture.ToString();
                    if (!Trigger.IsRegistered && !String.IsNullOrEmpty(Trigger.Error))
                    {
                        ToolTip.SetTip(_status, Trigger.Error);
                        _status.Background = Brushes.PaleVioletRed;
                    }
                    else
                    {
                        ToolTip.SetTip(_status, null);
                        _status.Background = Brushes.PaleGreen;
                    }
                }
            }
        }
    }
}
