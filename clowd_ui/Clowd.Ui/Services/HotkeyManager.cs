using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Clowd.Config;

namespace Clowd.UI
{
    /// <summary>Identifies one of the application's global hotkeys.</summary>
    public enum HotkeyId
    {
        FileUpload,
        ClipboardUpload,
        CaptureRegion,
        CaptureFullscreen,
        CaptureActive,
        StartStopRecording,
    }

    /// <summary>
    /// Live view of a single hotkey for the editor UI: the persisted gesture (writing it saves
    /// settings and re-registers immediately) plus the current OS registration status.
    /// </summary>
    public sealed class HotkeyEntry : SimpleNotifyObject
    {
        public HotkeyId Id { get; }

        /// <summary>Name of the corresponding <see cref="SettingsHotkey"/> property.</summary>
        public string SettingsProperty { get; }

        /// <summary>
        /// The persisted gesture. Setting it writes through to <see cref="SettingsHotkey"/>,
        /// saves the settings file and rebinds the OS registration.
        /// </summary>
        public SimpleKeyGesture Gesture
        {
            get => _manager.GetGesture(this);
            set => _manager.SetGesture(this, value);
        }

        public bool IsRegistered
        {
            get => _isRegistered;
            private set => Set(ref _isRegistered, value);
        }

        public string Error
        {
            get => _error;
            private set => Set(ref _error, value);
        }

        private readonly HotkeyManager _manager;
        internal readonly Func<SettingsHotkey, SimpleKeyGesture> Getter;
        internal readonly Action<SettingsHotkey, SimpleKeyGesture> Setter;
        private IGlobalTriggerRegistration _registration;
        private bool _isRegistered;
        private string _error = "";

        internal HotkeyEntry(HotkeyManager manager, HotkeyId id, string settingsProperty,
            Func<SettingsHotkey, SimpleKeyGesture> getter, Action<SettingsHotkey, SimpleKeyGesture> setter)
        {
            _manager = manager;
            Id = id;
            SettingsProperty = settingsProperty;
            Getter = getter;
            Setter = setter;
        }

        internal void RaiseGestureChanged() => OnPropertyChanged(nameof(Gesture));

        internal void SetStatus(bool isRegistered, string error)
        {
            IsRegistered = isRegistered;
            Error = error ?? "";
        }

        internal void AttachRegistration(IGlobalTriggerRegistration registration)
        {
            _registration = registration;
            registration.StatusChanged += OnRegistrationStatusChanged;
            SetStatus(registration.IsRegistered, registration.Error);
        }

        internal void DetachRegistration()
        {
            if (_registration == null)
                return;

            _registration.StatusChanged -= OnRegistrationStatusChanged;
            _registration.Dispose();
            _registration = null;
        }

        private void OnRegistrationStatusChanged(object sender, EventArgs e)
        {
            if (ReferenceEquals(sender, _registration))
                SetStatus(_registration.IsRegistered, _registration.Error);
        }
    }

    /// <summary>
    /// Owns global hotkey registration, fully decoupled from the settings data classes: gestures
    /// are read from (and written to) <see cref="SettingsHotkey"/>, actions are wired explicitly
    /// at startup via <see cref="SetAction"/>, and live status is exposed per hotkey through
    /// <see cref="HotkeyEntry"/> for the editor UI.
    /// </summary>
    internal sealed class HotkeyManager : IDisposable
    {
        /// <summary>Assigned explicitly during application startup (like SettingsRoot.Current).</summary>
        public static HotkeyManager Current { get; set; }

        public IReadOnlyList<HotkeyEntry> Entries => _entries;

        /// <summary>While true, hotkeys neither fire nor swallow key presses (gesture editing).</summary>
        public bool IsPaused
        {
            get => _host.IsPaused;
            set => _host.IsPaused = value;
        }

        private readonly IGlobalTriggerHost _host;
        private readonly SettingsHotkey _settings;
        private readonly List<HotkeyEntry> _entries;
        private readonly Dictionary<HotkeyId, Action> _actions = new();
        private bool _disposed;

        public HotkeyManager(IGlobalTriggerHost host, SettingsHotkey settings)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _entries = new List<HotkeyEntry>
            {
                Entry(HotkeyId.FileUpload, nameof(SettingsHotkey.FileUploadShortcut), s => s.FileUploadShortcut, (s, g) => s.FileUploadShortcut = g),
                Entry(HotkeyId.ClipboardUpload, nameof(SettingsHotkey.ClipboardUploadShortcut), s => s.ClipboardUploadShortcut, (s, g) => s.ClipboardUploadShortcut = g),
                Entry(HotkeyId.CaptureRegion, nameof(SettingsHotkey.CaptureRegionShortcut), s => s.CaptureRegionShortcut, (s, g) => s.CaptureRegionShortcut = g),
                Entry(HotkeyId.CaptureFullscreen, nameof(SettingsHotkey.CaptureFullscreenShortcut), s => s.CaptureFullscreenShortcut, (s, g) => s.CaptureFullscreenShortcut = g),
                Entry(HotkeyId.CaptureActive, nameof(SettingsHotkey.CaptureActiveShortcut), s => s.CaptureActiveShortcut, (s, g) => s.CaptureActiveShortcut = g),
                Entry(HotkeyId.StartStopRecording, nameof(SettingsHotkey.StartStopRecordingShortcut), s => s.StartStopRecordingShortcut, (s, g) => s.StartStopRecordingShortcut = g),
            };

            HotkeyEntry Entry(HotkeyId id, string prop, Func<SettingsHotkey, SimpleKeyGesture> get, Action<SettingsHotkey, SimpleKeyGesture> set) =>
                new HotkeyEntry(this, id, prop, get, set);
        }

        /// <summary>Wires the action invoked when the hotkey fires, then (re)registers it.</summary>
        public void SetAction(HotkeyId id, Action action)
        {
            _actions[id] = action;
            Rebind(GetEntry(id));
        }

        public HotkeyEntry GetEntry(HotkeyId id) => _entries.First(e => e.Id == id);

        /// <summary>Finds the entry for a <see cref="SettingsHotkey"/> property name (used by the
        /// reflection-driven settings page).</summary>
        public HotkeyEntry GetEntryForProperty(string settingsProperty) =>
            _entries.FirstOrDefault(e => e.SettingsProperty == settingsProperty);

        /// <summary>Re-registers every hotkey from the current settings gestures.</summary>
        public void Refresh()
        {
            foreach (var entry in _entries)
                Rebind(entry);
        }

        internal SimpleKeyGesture GetGesture(HotkeyEntry entry) => entry.Getter(_settings);

        internal void SetGesture(HotkeyEntry entry, SimpleKeyGesture gesture)
        {
            if (Equals(GetGesture(entry), gesture))
                return;

            entry.Setter(_settings, gesture);

            // explicit-save policy: persisting the gesture is a UI-layer responsibility.
            if (SettingsRoot.Current != null)
                SettingsService.Save(SettingsRoot.Current);

            Rebind(entry);
            entry.RaiseGestureChanged();
        }

        private void Rebind(HotkeyEntry entry)
        {
            entry.DetachRegistration();

            if (_disposed)
                return;

            if (!_actions.TryGetValue(entry.Id, out var action) || action == null)
            {
                entry.SetStatus(false, "");
                return;
            }

            var gesture = GetGesture(entry);
            if (gesture == null || gesture.Key == Key.None)
            {
                entry.SetStatus(false, "Gesture is empty.");
                return;
            }

            entry.AttachRegistration(_host.RegisterTrigger(gesture, action));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (var entry in _entries)
            {
                entry.DetachRegistration();
                entry.SetStatus(false, "");
            }

            (_host as IDisposable)?.Dispose();

            if (Current == this)
                Current = null;
        }
    }
}
