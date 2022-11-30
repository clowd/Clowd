using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Clowd.Avalonia.Converters;
using Clowd.Avalonia.Extensions;
using Clowd.Avalonia.Services;
using Clowd.Config;
using Clowd.PlatformUtil.Windows;
using DependencyPropertyGenerator;
using FluentAvalonia.UI.Controls;
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

            _hotkey = new Button()
            {
                [!Button.ContentProperty] = new Binding(nameof(HotkeyRegistration.KeyGestureText)),
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
            };

            _hotkey.KeyDown += hotkey_KeyDown;
            _hotkey.KeyUp += hotkey_KeyUp;
            _hotkey.Click += hotkey_Click;
            _hotkey.LostFocus += hotkey_LostFocus;

            _refresh = new Button()
            {
                Content = new SymbolIcon() { Symbol = Symbol.RefreshFilled },
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch,
                [!Button.IsVisibleProperty] = new Binding(nameof(HotkeyRegistration.Status)) { Converter = new EnumMatchToBooleanConverter(), ConverterParameter = nameof(HotkeyRegistrationStatus.Error) },
            };

            _refresh.Click += refresh_Click;

            DockPanel.SetDock(_refresh, Dock.Right);

            var sp = new DockPanel();
            sp.MinWidth = 250;
            sp.Children.Add(_refresh);
            sp.Children.Add(_hotkey);

            _error = new TextBlock()
            {
                [!TextBlock.TextProperty] = new Binding(nameof(HotkeyRegistration.Error)),
                [!TextBlock.IsVisibleProperty] = new Binding(nameof(HotkeyRegistration.Error)) { Converter = StringConverters.IsNotNullOrEmpty },
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SystemFillColorCriticalBrush"),
                Margin = new Thickness(0, 8, 0, 0),
            };

            _card = new SettingsCard()
            {
                [!SettingsCard.TitleProperty] = new Binding(nameof(HotkeyRegistration.Name)),
                ActionContent = sp,
                BottomContent = _error,
            };

            Content = _card;

            UpdateBackground();
        }

        private void refresh_Click(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            Hotkey?.Retry();
        }

        private void hotkey_LostFocus(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            IsEditing = false;
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
                _hotkey.Content = KeyInterop.GetShortString(GestureKey.None, (GestureModifierKeys)e.KeyModifiers);
            }
            else
            {
                FinishEditing(e.Key, e.KeyModifiers);
            }
        }

        private void FinishEditing(Key key, KeyModifiers keyModifiers)
        {
            IsEditing = false;
            if (key != Key.Escape)
            {
                Hotkey.Gesture = new SimpleKeyGesture((GestureKey)key, (GestureModifierKeys)keyModifiers);
            }
            else
            {
                Hotkey.Gesture = new SimpleKeyGesture();
            }
        }

        void IObserver<IReactivePropertyChangedEventArgs<IReactiveObject>>.OnCompleted()
        {
        }

        void IObserver<IReactivePropertyChangedEventArgs<IReactiveObject>>.OnError(Exception error)
        {
        }

        void IObserver<IReactivePropertyChangedEventArgs<IReactiveObject>>.OnNext(IReactivePropertyChangedEventArgs<IReactiveObject> value)
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
