using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Clowd.Avalonia.Extensions;
using Clowd.Avalonia.ViewModels;
using Clowd.Config;
using Clowd.Localization;
using Clowd.PlatformUtil;
using Clowd.PlatformUtil.Windows;
using DynamicData.Binding;
using PropertyChanged.SourceGenerator;

namespace Clowd.Avalonia.Services
{
    public class HotkeyRegistrationService
    {
        public static bool IsPaused { get; set; }

        private List<HotkeyRegistration> _registrations;

        public HotkeyRegistrationService()
        {
            _registrations = new() {
                new HotkeyRegistration(StringsKeys.SettingsHotkey_CaptureRegion, k => k.CaptureRegionShortcut, OnCaptureRegionExecuted),
                new HotkeyRegistration(StringsKeys.SettingsHotkey_CaptureMonitor, k => k.CaptureFullscreenShortcut, OnCaptureFullscreenExecuted),
                new HotkeyRegistration(StringsKeys.SettingsHotkey_CaptureWindow, k => k.CaptureActiveShortcut, OnCaptureActiveExecuted),
                new HotkeyRegistration(StringsKeys.SettingsHotkey_DrawOnScreen, k => k.DrawOnScreenShortcut, OnDrawOnScreenExecuted),
                new HotkeyRegistration(StringsKeys.SettingsHotkey_StartStopRec, k => k.StartStopRecordingShortcut, OnStartStopRecordingExecuted),
                new HotkeyRegistration(StringsKeys.SettingsHotkey_UploadFile, k => k.FileUploadShortcut, OnFileUploadExecuted),
                new HotkeyRegistration(StringsKeys.SettingsHotkey_UploadClipboard, k => k.ClipboardUploadShortcut, OnClipboardUploadExecuted),
            };
        }

        public IEnumerable<HotkeyRegistration> GetRegistrations() => _registrations;

        void OnStartStopRecordingExecuted()
        {
        }

        void OnFileUploadExecuted()
        {
        }

        void OnClipboardUploadExecuted()
        {
        }

        void OnCaptureRegionExecuted()
        {
        }

        void OnCaptureFullscreenExecuted()
        {
        }

        void OnCaptureActiveExecuted()
        {
        }

        void OnDrawOnScreenExecuted()
        {
        }
    }

    public enum HotkeyRegistrationStatus
    {
        Unset,
        Error,
        Success,
    }

    public partial class HotkeyRegistration : CultureAwareViewModel
    {
        public string Name => Strings.GetString(_name);

        public string KeyGestureText => IsUnset ? Strings.SettingsHotkey_Unset : KeyInterop.GetShortString(Gesture);

        public bool IsUnset => Gesture == null || Gesture.IsEmpty;

        public HotkeyRegistrationStatus Status =>
            IsUnset ? HotkeyRegistrationStatus.Unset : IsRegistered ? HotkeyRegistrationStatus.Success : HotkeyRegistrationStatus.Error;

        public SimpleKeyGesture Gesture
        {
            get => SettingsRoot.Current.Hotkeys.GetPropertyValue(_property);
            set => SettingsRoot.Current.Hotkeys.SetPropertyValue(_property, value);
        }

        [Notify(Setter.Private)] bool _isRegistered;
        [Notify(Setter.Private)] string _error;

        private IDisposable _registration;
        private readonly Action _execute;
        private readonly StringsKeys _name;
        private readonly Expression<Func<SettingsHotkey, SimpleKeyGesture>> _property;

        public HotkeyRegistration(StringsKeys name, Expression<Func<SettingsHotkey, SimpleKeyGesture>> property, Action execute)
        {
            _name = name;
            _property = property;
            _execute = execute;
            SettingsRoot.Current.Hotkeys.WhenPropertyChanged(_property).Subscribe((v) =>
            {
                OnPropertyChanged(nameof(Gesture));
                OnPropertyChanged(nameof(KeyGestureText));
                OnGestureChanged();
            });
            OnGestureChanged();
        }

        public void Retry()
        {
            if (!IsRegistered)
            {
                OnGestureChanged();
            }
        }

        private bool IsBlacklisted(SimpleKeyGesture gesture)
        {
            if (gesture.Key == GestureKey.None)
                return true;

            if (gesture.Key == GestureKey.Escape && gesture.Modifiers == GestureModifierKeys.None)
                return true;

            return false;
        }

        private void OnGestureChanged()
        {
            _registration?.Dispose();

            try
            {
                Error = null;
                IsRegistered = false;
                if (IsUnset) return;

                if (!IsBlacklisted(Gesture))
                {
                    _registration = Platform.Current.RegisterHotKey(Gesture.Key, Gesture.Modifiers, OnExecuted);
                    IsRegistered = true;
                }
                else
                {
                    Error = Strings.SettingsHotkey_InvalidGesture;
                }
            }
            catch (Exception e)
            {
                Error = e.Message;
            }
        }

        private void OnExecuted()
        {
            if (!HotkeyRegistrationService.IsPaused)
            {
                _execute();
            }
        }
    }
}
