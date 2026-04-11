using System;
using System.Collections.Generic;
using System.ComponentModel;
using Clowd.Ui.Models.Settings;

namespace Clowd.Ui.Services;

/// <summary>
/// Wires each <see cref="GlobalTrigger"/> in <see cref="SettingsHotkey"/> to a callback through
/// <see cref="IPlatformService.RegisterGlobalHotkey"/>. Listens to <see cref="GlobalTrigger"/>
/// property changes so that when the user edits a shortcut in the settings page the underlying
/// SharpHook registration is disposed and a fresh one installed.
/// </summary>
/// <remarks>
/// Known limitation: no gesture-collision detection. If two triggers share the same key+modifiers,
/// both callbacks fire. The WPF version surfaced a validation error but this port doesn't yet.
/// </remarks>
public sealed class HotkeyBinder : IDisposable
{
    private readonly IPlatformService _platform;
    private readonly List<Binding> _bindings = new();
    private bool _disposed;

    public HotkeyBinder(IPlatformService platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    /// <summary>
    /// Bind <paramref name="trigger"/> to <paramref name="callback"/>. The current gesture (if any)
    /// is registered immediately, and any subsequent changes to <see cref="GlobalTrigger.KeyGesture"/>
    /// cause the OS-level registration to be replaced automatically.
    /// </summary>
    public void Bind(GlobalTrigger trigger, Action callback)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed) throw new ObjectDisposedException(nameof(HotkeyBinder));

        var binding = new Binding(_platform, trigger, callback);
        _bindings.Add(binding);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var b in _bindings) b.Dispose();
        _bindings.Clear();
    }

    private sealed class Binding : IDisposable
    {
        private readonly IPlatformService _platform;
        private readonly GlobalTrigger _trigger;
        private readonly Action _callback;
        private IDisposable? _registration;
        private bool _disposed;

        public Binding(IPlatformService platform, GlobalTrigger trigger, Action callback)
        {
            _platform = platform;
            _trigger = trigger;
            _callback = callback;
            _trigger.PropertyChanged += OnTriggerPropertyChanged;
            Refresh();
        }

        private void OnTriggerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GlobalTrigger.KeyGesture))
                Refresh();
        }

        private void Refresh()
        {
            _registration?.Dispose();
            _registration = null;

            if (_disposed) return;

            var gesture = _trigger.KeyGesture;
            if (gesture is null || gesture.Key == Avalonia.Input.Key.None) return;

            try
            {
                _registration = _platform.RegisterGlobalHotkey(gesture, _callback);
            }
            catch (NotImplementedException)
            {
                // Platform doesn't support global hotkeys at all — swallow so the app still runs.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HotkeyBinder] failed to register '{gesture}': {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _trigger.PropertyChanged -= OnTriggerPropertyChanged;
            _registration?.Dispose();
            _registration = null;
        }
    }
}
