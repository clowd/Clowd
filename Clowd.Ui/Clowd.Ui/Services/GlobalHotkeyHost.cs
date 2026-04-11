using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Ui.Models.Settings;
using SharpHook;
using SharpHook.Data;

namespace Clowd.Ui.Services;

/// <summary>
/// Owns a single libuiohook-backed <see cref="SimpleGlobalHook"/> and dispatches keyboard events
/// to registered hotkey callbacks. SharpHook requires that only one IGlobalHook exist per process,
/// so <see cref="Clowd.Ui.Services.DefaultPlatformService"/> shares one instance for all
/// <c>RegisterGlobalHotkey</c> calls.
/// </summary>
/// <remarks>
/// Threading: <see cref="SimpleGlobalHook"/> invokes its event handlers on the dedicated hook thread,
/// which is exactly what we need for <see cref="HookEventArgs.SuppressEvent"/> to take effect
/// synchronously. Registered user callbacks are marshalled to the Avalonia UI thread via
/// <see cref="Dispatcher.UIThread"/> so they can safely touch Avalonia controls.
/// </remarks>
internal sealed class GlobalHotkeyHost : IDisposable
{
    private readonly object _gate = new();
    private SimpleGlobalHook? _hook;
    private Task? _hookTask;
    private bool _disposed;

    // Copy-on-write list: the event-handler read path snapshots the reference, mutations allocate a
    // new list under the lock. Registrations are rare, events are frequent.
    private volatile List<Entry> _entries = new();

    private sealed record Entry(KeyCode ExpectedKey, Avalonia.Input.KeyModifiers ExpectedModifiers, Action Callback);

    /// <summary>
    /// Register <paramref name="callback"/> to fire when the user presses <paramref name="gesture"/>.
    /// Returns an <see cref="IDisposable"/> that removes the registration when disposed — used by
    /// <see cref="HotkeyBinder"/> to reassign shortcuts as the user edits them in settings.
    /// </summary>
    public IDisposable Register(SimpleKeyGesture gesture, Action callback)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ArgumentNullException.ThrowIfNull(callback);

        var keyCode = SharpHookKeyMap.TryMapKey(gesture.Key);
        if (keyCode is null)
        {
            Debug.WriteLine($"[GlobalHotkeyHost] ignoring unmappable key '{gesture.Key}'");
            return NullDisposable.Instance;
        }

        var entry = new Entry(keyCode.Value, gesture.Modifiers, callback);

        lock (_gate)
        {
            if (_disposed)
                return NullDisposable.Instance;

            var next = new List<Entry>(_entries.Count + 1);
            next.AddRange(_entries);
            next.Add(entry);
            _entries = next;

            EnsureHookStarted();
        }

        return new Registration(this, entry);
    }

    private void Unregister(Entry entry)
    {
        lock (_gate)
        {
            if (_disposed) return;

            var next = new List<Entry>(_entries);
            next.Remove(entry);
            _entries = next;
        }
    }

    // Must be called under _gate.
    private void EnsureHookStarted()
    {
        if (_hook is not null) return;

        try
        {
            var hook = new SimpleGlobalHook(GlobalHookType.Keyboard);
            hook.KeyPressed += OnKeyPressed;
            _hook = hook;
            _hookTask = hook.RunAsync();
            _hookTask.ContinueWith(
                static t => Debug.WriteLine($"[GlobalHotkeyHost] hook stopped: {t.Exception?.Flatten().Message ?? "normal exit"}"),
                TaskContinuationOptions.OnlyOnFaulted);
            Debug.WriteLine("[GlobalHotkeyHost] SharpHook started");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GlobalHotkeyHost] failed to start SharpHook: {ex}");
            _hook = null;
            _hookTask = null;
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        // Snapshot — safe because the list is replaced, never mutated in place.
        var entries = _entries;
        if (entries.Count == 0) return;

        var incomingKey = e.Data.KeyCode;
        var incomingMask = e.RawEvent.Mask;

        foreach (var entry in entries)
        {
            if (entry.ExpectedKey != incomingKey) continue;
            if (!SharpHookKeyMap.ModifiersMatch(incomingMask, entry.ExpectedModifiers)) continue;

            // Suppress BEFORE marshalling — must happen on the hook thread to take effect. This stops
            // e.g. PrintScreen from also firing Windows' built-in screenshot-to-clipboard shim.
            e.SuppressEvent = true;

            var callback = entry.Callback;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GlobalHotkeyHost] hotkey callback threw: {ex}");
                }
            });
            // Keep looping: a user could conceivably register two callbacks for the same gesture.
        }
    }

    public void Dispose()
    {
        SimpleGlobalHook? hook;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _entries = new List<Entry>();
            hook = _hook;
            _hook = null;
        }

        if (hook is not null)
        {
            hook.KeyPressed -= OnKeyPressed;
            try { hook.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[GlobalHotkeyHost] hook dispose threw: {ex}"); }
        }
    }

    private sealed class Registration : IDisposable
    {
        private GlobalHotkeyHost? _host;
        private readonly Entry _entry;

        public Registration(GlobalHotkeyHost host, Entry entry)
        {
            _host = host;
            _entry = entry;
        }

        public void Dispose()
        {
            var host = System.Threading.Interlocked.Exchange(ref _host, null);
            host?.Unregister(_entry);
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
