using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Clowd.Avalonia.Extensions;
using Clowd.Avalonia.Services;
using Clowd.Config;
using DependencyPropertyGenerator;
using ReactiveUI;

namespace Clowd.Avalonia.Controls
{
    [DependencyProperty<bool>("IsEditing")]
    [DependencyProperty<HotkeyRegistration>("Hotkey")]
    public partial class HotkeyEditor : UserControl, IObserver<IReactivePropertyChangedEventArgs<IReactiveObject>>
    {
        private SettingsCard _card;
        private Button _refresh;
        private Button _hotkey;
        private TextBlock _error;

        private IDisposable _subscription;

        public HotkeyEditor()
        {
            DataContext = Hotkey;

            _card = new SettingsCard();
            _card.Bind(SettingsCard.TitleProperty, new Binding(nameof(HotkeyRegistration.Name)));

            _hotkey = new Button();
            _hotkey.MinWidth = 250;
            _hotkey.Bind(Button.ContentProperty, new Binding(nameof(HotkeyRegistration.KeyGestureText)));
            _hotkey.KeyDown += hotkey_KeyDown;
            _hotkey.KeyUp += hotkey_KeyUp;
            _hotkey.Click += hotkey_Click;

            _error = new TextBlock();
            _error.Bind(TextBlock.TextProperty, new Binding(nameof(HotkeyRegistration.Error)));
            _error[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SystemFillColorCriticalBrush");
            _error.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(HotkeyRegistration.Error)) { Converter = StringConverters.IsNotNullOrEmpty });
            _error.Margin = new Thickness(0, 8, 0, 0);

            _card.ActionContent = _hotkey;
            _card.BottomContent = _error;

            Content = _card;

            UpdateBackground();
        }

        private void hotkey_KeyUp(object sender, KeyEventArgs e)
        {
            if (!IsEditing) return;
            HandleKey(e);
        }

        private void hotkey_Click(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            IsEditing = true;
        }

        private void hotkey_KeyDown(object sender, KeyEventArgs e)
        {
            if (!IsEditing) return;
            HandleKey(e);
        }

        private void HandleKey(KeyEventArgs e)
        {
            var keyCode = (int)e.Key;
            if (keyCode == 70 || keyCode == 71 || (keyCode >= 116 && keyCode <= 121))
            {
                StringBuilder key = new StringBuilder();
                foreach (var en in GetUniqueFlags(e.KeyModifiers))
                {
                    key.Append(((KeyModifiers)en).ToString().Replace("Control", "Ctrl"));
                    key.Append('+');
                }

                key.Append(" ...");
                _hotkey.Content = key.ToString();
            }
            else
            {
                FinishEditing(e.Key, e.KeyModifiers);
            }
        }

        private void FinishEditing(Key key, KeyModifiers keyModifiers)
        {
            Hotkey.Gesture = new SimpleKeyGesture((GestureKey)key, (GestureModifierKeys)keyModifiers);
            IsEditing = false;
        }

        private IEnumerable<Enum> GetUniqueFlags(Enum flags)
        {
            ulong flag = 1;
            foreach (var value in Enum.GetValues(flags.GetType()).Cast<Enum>())
            {
                ulong bits = Convert.ToUInt64(value);
                while (flag < bits)
                {
                    flag <<= 1;
                }

                if (flag == bits && flags.HasFlag(value))
                {
                    yield return value;
                }
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(IReactivePropertyChangedEventArgs<IReactiveObject> value)
        {
            UpdateBackground();
        }

        partial void OnIsEditingChanged(bool newValue)
        {
            if (newValue)
            {
                _hotkey.Focus();
                _hotkey.Content = "...";
            }
            else
            {
                _hotkey.Bind(Button.ContentProperty, new Binding(nameof(HotkeyRegistration.KeyGestureText)));
            }

            UpdateBackground();
        }

        private void UpdateBackground()
        {
            if (IsEditing)
            {
                _card[!SettingsCard.BackgroundProperty] = new DynamicResourceExtension("ControlSolidFillColorDefault");
            }
            else if (Hotkey?.IsUnset == true)
            {
                _card[!SettingsCard.BackgroundProperty] = new DynamicResourceExtension("SystemFillColorCautionBackgroundBrush");
            }
            else if (Hotkey?.IsRegistered == true)
            {
                _card[!SettingsCard.BackgroundProperty] = new DynamicResourceExtension("ControlFillColorDefault");
            }
            else
            {
                _card[!SettingsCard.BackgroundProperty] = new DynamicResourceExtension("SystemFillColorCriticalBackgroundBrush");
            }
        }

        partial void OnHotkeyChanged(HotkeyRegistration newValue)
        {
            DataContext = newValue;
            _subscription?.Dispose();
            if (newValue != null)
            {
                _subscription = newValue.Changed.ToWeakObservable().Subscribe(this);
                UpdateBackground();
            }
        }
    }
}
