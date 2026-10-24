using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.Config;

namespace Clowd.UI
{
    /// <summary>Identifies one of the application's global hotkey actions.</summary>
    public enum HotkeyId
    {
        FileUpload,
        ClipboardUpload,
        CaptureRegion,
        CaptureFullscreen,
        CaptureActive,
        StartStopRecording,
        ShareRegion,
        DrawOnScreen,
        DrawOnScreenshot,
        ColorWheel,
        NewImageEditor,
        NewVideoEditor,
    }

    /// <summary>
    /// One key combination of a <see cref="HotkeyEntry"/> and its own live registration status:
    /// an action's gestures register independently, so one can fail (taken by another app, a key
    /// the backend cannot hook) while the rest work. Kept for as long as the gesture stays in the
    /// action's list — re-registration (a feature switch, an owner change) updates this same
    /// object, so a bound view only needs to follow its PropertyChanged.
    /// </summary>
    public sealed class HotkeyShortcut : SimpleNotifyObject
    {
        public SimpleKeyGesture Gesture { get; }

        /// <summary>True while the gesture is actually hooked and will fire the action.</summary>
        public bool IsRegistered
        {
            get => _isRegistered;
            private set => Set(ref _isRegistered, value);
        }

        /// <summary>Why the gesture is not registered; empty when it is, or when nobody owns the
        /// action right now (no handler wired, or a status still to come from the capturer).</summary>
        public string Error
        {
            get => _error;
            private set => Set(ref _error, value);
        }

        internal event EventHandler StatusChanged;

        private IGlobalTriggerRegistration _registration;
        private bool _isRegistered;
        private string _error = "";

        internal HotkeyShortcut(SimpleKeyGesture gesture)
        {
            Gesture = gesture;
        }

        internal void SetStatus(bool isRegistered, string error)
        {
            error ??= "";
            if (IsRegistered == isRegistered && Error == error)
                return;

            IsRegistered = isRegistered;
            Error = error;
            StatusChanged?.Invoke(this, EventArgs.Empty);
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
    /// Live view of one hotkey action for the editor UI: its persisted gestures (every change saves
    /// settings and re-registers immediately) and, per gesture, the current OS registration status.
    /// </summary>
    public sealed class HotkeyEntry : SimpleNotifyObject
    {
        public HotkeyId Id { get; }

        /// <summary>Name of the corresponding <see cref="SettingsHotkey"/> property.</summary>
        public string SettingsProperty { get; }

        /// <summary>
        /// The persisted gestures, in order. Setting them writes through to
        /// <see cref="SettingsHotkey"/>, saves the settings file and rebinds the OS registrations;
        /// a gesture another action holds is taken from it (see <see cref="AddGesture"/>).
        /// </summary>
        public GestureList Gestures
        {
            get => _manager.GetGestures(this);
            set => _manager.SetGestures(this, value);
        }

        /// <summary>One <see cref="HotkeyShortcut"/> per gesture, in the same order as
        /// <see cref="Gestures"/>. A new list (raising PropertyChanged) whenever the gestures change;
        /// status changes are raised by the shortcuts themselves.</summary>
        public IReadOnlyList<HotkeyShortcut> Shortcuts => _shortcuts;

        /// <summary>The first gesture that is actually registered — what the tray menu shows beside
        /// the action — or null when none is.</summary>
        public SimpleKeyGesture FirstRegisteredGesture => _shortcuts.FirstOrDefault(s => s.IsRegistered)?.Gesture;

        /// <summary>False while the action's feature is switched off (see
        /// <see cref="HotkeyManager.SetEnabled"/>): its gestures are kept but none is registered.</summary>
        public bool IsEnabled => _manager.IsEnabled(Id);

        /// <summary>Raised when the gestures or any of their statuses change.</summary>
        internal event EventHandler Changed;

        private readonly HotkeyManager _manager;
        internal readonly Func<SettingsHotkey, GestureList> Getter;
        internal readonly Action<SettingsHotkey, GestureList> Setter;
        private HotkeyShortcut[] _shortcuts = Array.Empty<HotkeyShortcut>();

        internal HotkeyEntry(HotkeyManager manager, HotkeyId id, string settingsProperty,
            Func<SettingsHotkey, GestureList> getter, Action<SettingsHotkey, GestureList> setter)
        {
            _manager = manager;
            Id = id;
            SettingsProperty = settingsProperty;
            Getter = getter;
            Setter = setter;
        }

        /// <summary>Appends <paramref name="gesture"/>, taking it from whichever other action held
        /// it (hidden, feature-switched rows included — it would otherwise collide the moment the
        /// feature came back). A no-op when this action already has it.</summary>
        public void AddGesture(SimpleKeyGesture gesture) => Gestures = Gestures.Add(gesture);

        /// <summary>Replaces the gesture at <paramref name="index"/> in place, taking the new one from
        /// any other action as <see cref="AddGesture"/> does. A no-op when this action already holds
        /// <paramref name="gesture"/> at any index; a null or <see cref="Avalonia.Input.Key.None"/>
        /// gesture removes the one at <paramref name="index"/>.</summary>
        public void ReplaceGesture(int index, SimpleKeyGesture gesture) => Gestures = Gestures.SetItem(index, gesture);

        public void RemoveGestureAt(int index) => Gestures = Gestures.RemoveAt(index);

        /// <summary>
        /// Brings <see cref="Shortcuts"/> in line with the persisted gestures, keeping the shortcut
        /// object (and so its status and any view bound to it) of every gesture still in the list.
        /// Removed gestures lose their OS registration here.
        /// </summary>
        internal void SyncShortcuts(GestureList gestures)
        {
            if (_shortcuts.Select(s => s.Gesture).SequenceEqual(gestures))
                return;

            var kept = new List<HotkeyShortcut>();
            foreach (var gesture in gestures)
            {
                var shortcut = _shortcuts.FirstOrDefault(s => Equals(s.Gesture, gesture));
                if (shortcut == null)
                {
                    shortcut = new HotkeyShortcut(gesture);
                    shortcut.StatusChanged += OnShortcutStatusChanged;
                }
                kept.Add(shortcut);
            }

            foreach (var removed in _shortcuts.Except(kept))
            {
                removed.StatusChanged -= OnShortcutStatusChanged;
                removed.DetachRegistration();
            }

            _shortcuts = kept.ToArray();
            OnPropertyChanged(nameof(Gestures));
            OnPropertyChanged(nameof(Shortcuts));
            OnPropertyChanged(nameof(FirstRegisteredGesture));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        internal void RaiseEnabledChanged() => OnPropertyChanged(nameof(IsEnabled));

        private void OnShortcutStatusChanged(object sender, EventArgs e)
        {
            OnPropertyChanged(nameof(FirstRegisteredGesture));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Owns global hotkey registration, fully decoupled from the settings data classes: gestures
    /// are read from (and written to) <see cref="SettingsHotkey"/>, actions are wired explicitly
    /// at startup via <see cref="SetAction"/>, and live status is exposed per gesture through
    /// <see cref="HotkeyEntry.Shortcuts"/> for the editor UI.
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

        /// <summary>The OS backend, for features that listen for a fixed key of their own rather
        /// than a user-configurable gesture (the video editor's media keys). Registrations made
        /// through it share this manager's <see cref="IsPaused"/> state and the same hook.</summary>
        public IGlobalTriggerHost Host => _host;

        /// <summary>Raised whenever any action's gestures or any gesture's registration status
        /// change — the latter possibly long after the change that caused it (the SharpHook hook
        /// failing to start, the standby capturer reporting back). Raised on the thread that made
        /// the change, which is the UI thread in the app.</summary>
        public event EventHandler Changed;

        private readonly IGlobalTriggerHost _host;
        private readonly SettingsHotkey _settings;
        private readonly List<HotkeyEntry> _entries;
        private readonly Dictionary<HotkeyId, Action> _actions = new();
        private readonly HashSet<HotkeyId> _switchedOff = new();
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
                // ships with no default gesture (see SettingsHotkey), so this entry exists purely so
                // the settings page can offer one — Rebind leaves it unregistered until it has one.
                Entry(HotkeyId.ShareRegion, nameof(SettingsHotkey.ShareRegionShortcut), s => s.ShareRegionShortcut, (s, g) => s.ShareRegionShortcut = g),
                // no default gesture for any of the rest either (see SettingsHotkey)
                Entry(HotkeyId.DrawOnScreen, nameof(SettingsHotkey.DrawOnScreenShortcut), s => s.DrawOnScreenShortcut, (s, g) => s.DrawOnScreenShortcut = g),
                Entry(HotkeyId.DrawOnScreenshot, nameof(SettingsHotkey.DrawOnScreenshotShortcut), s => s.DrawOnScreenshotShortcut, (s, g) => s.DrawOnScreenshotShortcut = g),
                Entry(HotkeyId.ColorWheel, nameof(SettingsHotkey.ColorWheelShortcut), s => s.ColorWheelShortcut, (s, g) => s.ColorWheelShortcut = g),
                Entry(HotkeyId.NewImageEditor, nameof(SettingsHotkey.NewImageEditorShortcut), s => s.NewImageEditorShortcut, (s, g) => s.NewImageEditorShortcut = g),
                Entry(HotkeyId.NewVideoEditor, nameof(SettingsHotkey.NewVideoEditorShortcut), s => s.NewVideoEditorShortcut, (s, g) => s.NewVideoEditorShortcut = g),
            };

            foreach (var entry in _entries)
            {
                entry.SyncShortcuts(GetGestures(entry));
                entry.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            }

            HotkeyEntry Entry(HotkeyId id, string prop, Func<SettingsHotkey, GestureList> get, Action<SettingsHotkey, GestureList> set) =>
                new HotkeyEntry(this, id, prop, get, set);
        }

        /// <summary>Wires the action invoked when the hotkey fires, then (re)registers it. A null
        /// action leaves the gestures to another owner (the standby capturer, for the screenshot
        /// actions), which reports their status through <see cref="ApplyExternalStatus"/>.</summary>
        public void SetAction(HotkeyId id, Action action)
        {
            _actions[id] = action;
            Rebind(GetEntry(id));
        }

        /// <summary>
        /// Feature switch for a hotkey: the upload hotkeys while uploads are off, the recording
        /// and video editor hotkeys while recording is off. A switched-off hotkey is unregistered —
        /// not merely inert — so its gestures are neither swallowed nor shown as active; the
        /// gestures themselves are kept, and come back when the feature does.
        /// </summary>
        public void SetEnabled(HotkeyId id, bool enabled)
        {
            var changed = enabled ? _switchedOff.Remove(id) : _switchedOff.Add(id);
            if (!changed)
                return;

            var entry = GetEntry(id);
            Rebind(entry);
            entry.RaiseEnabledChanged();
        }

        public bool IsEnabled(HotkeyId id) => !_switchedOff.Contains(id);

        public HotkeyEntry GetEntry(HotkeyId id) => _entries.First(e => e.Id == id);

        /// <summary>Finds the entry for a <see cref="SettingsHotkey"/> property name.</summary>
        public HotkeyEntry GetEntryForProperty(string settingsProperty) =>
            _entries.FirstOrDefault(e => e.SettingsProperty == settingsProperty);

        /// <summary>Re-registers every hotkey from the current settings gestures.</summary>
        public void Refresh()
        {
            foreach (var entry in _entries)
            {
                entry.SyncShortcuts(GetGestures(entry));
                Rebind(entry);
            }
        }

        /// <summary>
        /// Statuses for an action whose gestures another process registers (the standby capturer's
        /// screenshot hotkeys), keyed by the gesture as that process received it —
        /// <see cref="SimpleKeyGesture.ToCapturerString"/>. A gesture with no report keeps the
        /// status it has: the report may predate the settings snapshot that added it, and the
        /// snapshot's own report follows. Ignored while this manager owns the action itself.
        /// </summary>
        public void ApplyExternalStatus(HotkeyId id, IReadOnlyList<ExternalGestureStatus> statuses)
        {
            if (_disposed || statuses == null)
                return;

            var entry = GetEntry(id);
            if (_actions.TryGetValue(id, out var action) && action != null)
                return;

            // each report is used once: two gestures that are distinct here can still send the
            // same token (CapturerKeyMap sends both Pause and Cancel as "Pause"), and the second
            // of those is the one the capturer refuses as a duplicate.
            var used = new bool[statuses.Count];
            foreach (var shortcut in entry.Shortcuts)
            {
                var token = shortcut.Gesture.ToCapturerString();
                for (int i = 0; i < statuses.Count; i++)
                {
                    if (used[i] || statuses[i].Gesture != token)
                        continue;
                    used[i] = true;
                    shortcut.SetStatus(statuses[i].IsRegistered, statuses[i].Error);
                    break;
                }
            }
        }

        internal GestureList GetGestures(HotkeyEntry entry) => entry.Getter(_settings);

        internal void SetGestures(HotkeyEntry entry, GestureList gestures)
        {
            gestures ??= GestureList.Empty;
            if (Equals(GetGestures(entry), gestures))
                return;

            // a gesture belongs to one action: whichever other hotkey held one of these loses it,
            // and is rebound first so this entry's registration can take the OS binding. Rows
            // hidden by a feature switch are included — their gesture would otherwise collide the
            // moment the feature comes back.
            foreach (var other in _entries)
            {
                if (other == entry)
                    continue;

                var current = GetGestures(other);
                var remaining = current.Except(gestures);
                if (ReferenceEquals(remaining, current))
                    continue;

                other.Setter(_settings, remaining);
                other.SyncShortcuts(remaining);
                Rebind(other);
            }

            entry.Setter(_settings, gestures);

            // explicit-save policy: persisting the gesture is a UI-layer responsibility.
            if (SettingsRoot.Current != null)
                SettingsService.Save(SettingsRoot.Current);

            entry.SyncShortcuts(gestures);
            Rebind(entry);
        }

        private void Rebind(HotkeyEntry entry)
        {
            foreach (var shortcut in entry.Shortcuts)
                shortcut.DetachRegistration();

            if (_disposed)
                return;

            if (!_actions.TryGetValue(entry.Id, out var action) || action == null)
            {
                // nobody here owns the action: no status until whoever does reports one
                foreach (var shortcut in entry.Shortcuts)
                    shortcut.SetStatus(false, "");
                return;
            }

            if (_switchedOff.Contains(entry.Id))
            {
                foreach (var shortcut in entry.Shortcuts)
                    shortcut.SetStatus(false, "This feature is turned off in settings.");
                return;
            }

            foreach (var shortcut in entry.Shortcuts)
                shortcut.AttachRegistration(_host.RegisterTrigger(shortcut.Gesture, action));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (var shortcut in _entries.SelectMany(e => e.Shortcuts))
            {
                shortcut.DetachRegistration();
                shortcut.SetStatus(false, "");
            }

            (_host as IDisposable)?.Dispose();

            if (Current == this)
                Current = null;
        }
    }

    /// <summary>One gesture's status as reported by the process that registered it (see
    /// <see cref="HotkeyManager.ApplyExternalStatus"/>).</summary>
    /// <param name="Gesture">The gesture exactly as that process received it, in
    /// <see cref="SimpleKeyGesture.ToCapturerString"/> form.</param>
    public readonly record struct ExternalGestureStatus(string Gesture, bool IsRegistered, string Error);
}
