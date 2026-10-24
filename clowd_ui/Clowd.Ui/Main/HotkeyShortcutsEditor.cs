using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Clowd.UI.Helpers;

namespace Clowd.UI.Config
{
    /// <summary>
    /// The editor column of one Hotkeys page row: a <see cref="GestureField"/> per gesture of the
    /// action, in its order, and an "+ Add shortcut" button under them. Clicking a field re-records
    /// that gesture in place; the add button records a new one into a field appended at the end,
    /// which a cancel takes away again without touching the settings.
    /// </summary>
    /// <remarks>
    /// The fields follow <see cref="HotkeyEntry.Shortcuts"/> live, so a gesture another action
    /// takes leaves this row the moment it is taken, and each field keeps its shortcut (and its
    /// status) for as long as that gesture stays. The field that is listening is never taken out of
    /// the panel while it listens (see <see cref="ChildListSync"/>): the edit itself changes the
    /// list it sits in — the gesture being re-recorded leaves the action meanwhile, see
    /// <see cref="GestureListEdit"/> — and losing the focus would cancel it.
    /// <para>A new shortcut canceled by the focus moving elsewhere keeps its slot, blank and inert,
    /// until the press that moved the focus is over (see <see cref="HoldCanceledSlot"/>):
    /// everything under the slot would otherwise move up while the mouse button is still down, and
    /// the click the user aimed at a control further down the page would land beside it.</para>
    /// </remarks>
    public sealed class HotkeyShortcutsEditor : StackPanel
    {
        public static readonly DirectProperty<HotkeyShortcutsEditor, bool> IsEmptyProperty =
            AvaloniaProperty.RegisterDirect<HotkeyShortcutsEditor, bool>(nameof(IsEmpty), o => o.IsEmpty);

        /// <summary>The row shows no field at all — no shortcut and none being recorded — so the
        /// page can mute it. A field listening for the action's first shortcut counts as one: the
        /// row the user is working on should not stay faded.</summary>
        public bool IsEmpty
        {
            get => _isEmpty;
            private set => SetAndRaise(IsEmptyProperty, ref _isEmpty, value);
        }

        // the opacity a muted row's add button rests at; full again under the pointer or the
        // keyboard focus, so it still reads as the way to give the action a shortcut.
        private const double EmptyAddOpacity = 0.6;

        private readonly HotkeyEntry _entry;
        private bool _isEmpty;
        private readonly string _actionName;
        private readonly StackPanel _fieldsPanel;
        private readonly Button _addButton;
        private readonly Dictionary<HotkeyShortcut, GestureField> _fields = new(ReferenceEqualityComparer.Instance);
        private GestureField _pending;
        private GestureListEdit _edit;
        private GestureField _canceledSlot;
        private TopLevel _canceledSlotRoot;
        private bool _applying;
        private bool _subscribed;

        /// <param name="entry">The action edited.</param>
        /// <param name="actionName">Its display name, for the screen-reader names of the add button
        /// and the fields.</param>
        public HotkeyShortcutsEditor(HotkeyEntry entry, string actionName)
        {
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _actionName = actionName;
            Orientation = Orientation.Vertical;
            Spacing = 6;

            _fieldsPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
            Children.Add(_fieldsPanel);

            // the same words whether or not the action has a shortcut yet: an action can have
            // several, so "add" is always what this does.
            _addButton = new Button
            {
                Content = "+ Add shortcut",
                FontSize = 13,
                Padding = new Thickness(8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var app = Application.Current;
            if (app != null && app.TryGetResource("BorderlessButton", app.ActualThemeVariant, out var theme) && theme is ControlTheme borderless)
                _addButton.Theme = borderless;
            _addButton.Classes.Add("Primary");
            AutomationProperties.SetName(_addButton, "Add shortcut for " + actionName);
            _addButton.Click += OnAddClick;
            Children.Add(_addButton);

            Styles.Add(new Style(x => x.OfType<Button>().Class("muted")
                .Not(y => y.Class(":pointerover")).Not(y => y.Class(":focus-visible")))
            {
                Setters = { new Setter(OpacityProperty, EmptyAddOpacity) },
            });

            Refresh();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // subscribed only while shown, like GestureField: the entry lives as long as the app,
            // the settings window does not.
            if (!_subscribed)
            {
                _subscribed = true;
                _entry.PropertyChanged += OnEntryPropertyChanged;
            }

            Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_subscribed)
            {
                _subscribed = false;
                _entry.PropertyChanged -= OnEntryPropertyChanged;
            }

            // nothing is left to keep in place once the page is gone; the next attach refreshes
            ReleaseCanceledSlot();
        }

        private void OnEntryPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!String.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(HotkeyEntry.Shortcuts))
                return;

            if (Dispatcher.UIThread.CheckAccess())
                Refresh();
            else
                Dispatcher.UIThread.Post(Refresh);
        }

        /// <summary>Brings the fields in line with the action's shortcuts, plus the listening field
        /// in its slot.</summary>
        private void Refresh()
        {
            // a list this editor is assigning itself: the caller refreshes once it has placed the
            // listening field's new shortcut (see EndEdit).
            if (_applying)
                return;

            var shortcuts = _entry.Shortcuts;
            foreach (var gone in _fields.Keys.Where(s => !shortcuts.Contains(s)).ToList())
            {
                _fields[gone].Shortcut = null;
                _fields.Remove(gone);
            }

            var desired = shortcuts.Select(FieldFor).ToList<Control>();
            if (_pending != null)
                desired.Insert(_edit.SlotIndex(desired.Count), _pending);
            // the slot a canceled new shortcut was recorded in, which was the last line too
            if (_canceledSlot != null)
                desired.Add(_canceledSlot);

            ChildListSync.Sync(_fieldsPanel.Children, desired, _pending);

            // an empty stack would still be spaced from the add button; with no fields the add
            // button is the row's first line, and as tall as a field so the label lines up with it.
            _fieldsPanel.IsVisible = desired.Count > 0;
            _addButton.MinHeight = desired.Count > 0 ? 0 : 32;
            _addButton.Classes.Set("muted", desired.Count == 0);
            IsEmpty = desired.Count == 0;
        }

        private GestureField FieldFor(HotkeyShortcut shortcut)
        {
            if (_fields.TryGetValue(shortcut, out var field))
                return field;

            field = NewField();
            field.Shortcut = shortcut;
            _fields[shortcut] = field;
            return field;
        }

        private GestureField NewField()
        {
            var field = new GestureField(_actionName);
            field.EditRequested += OnFieldEditRequested;
            field.RemoveRequested += OnFieldRemoveRequested;
            field.ListeningEnded += OnFieldListeningEnded;
            return field;
        }

        private void OnFieldEditRequested(object sender, EventArgs e)
        {
            var field = (GestureField)sender;
            var index = IndexOfShortcut(field.Shortcut);
            if (_pending != null || index < 0 || !field.BeginListening())
                return;

            ReleaseCanceledSlot();
            _edit = GestureListEdit.Replace(_entry.Gestures, index);
            _pending = field;
            _fields.Remove(field.Shortcut);
            Apply(_edit.Listening);
            Refresh();
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (_pending != null)
                return;

            // a slot still held for the previous new shortcut is this one's slot: the new field
            // takes its place, so nothing below moves either way.
            ReleaseCanceledSlot();
            var field = NewField();
            _edit = GestureListEdit.Append(_entry.Gestures);
            _pending = field;
            Refresh();

            if (!field.BeginListening())
            {
                // another recording holds the hotkeys (or there are none): no field to keep
                _pending = null;
                _edit = null;
                Refresh();
            }
        }

        private void OnFieldRemoveRequested(object sender, EventArgs e)
        {
            var field = (GestureField)sender;
            var index = IndexOfShortcut(field.Shortcut);
            if (_pending != null || index < 0)
                return;

            // the focus was on the removed field's ✕; leave it somewhere sensible on the row
            var hadFocus = field.IsKeyboardFocusWithin;
            _entry.RemoveGestureAt(index);
            if (hadFocus)
                _addButton.Focus();
        }

        private void OnFieldListeningEnded(object sender, SimpleKeyGesture gesture)
        {
            var field = (GestureField)sender;
            if (!ReferenceEquals(field, _pending))
                return;

            var edit = _edit;
            _pending = null;
            _edit = null;

            GestureList result;
            try
            {
                result = edit.Commit(gesture);
                Apply(result);
            }
            catch (Exception ex)
            {
                // a gesture that cannot be stored is treated as a canceled edit — the binding it
                // was replacing must survive it.
                Debug.WriteLine("Failed to store the recorded hotkey: " + ex);
                result = edit.Saved;
                Apply(result);
            }

            // the field that did the recording stays where it is, now standing for what it
            // recorded (or the gesture it gave back), so the focus stays on it too. A canceled
            // new shortcut has nothing to stand for, and leaves with the refresh.
            var index = edit.ResultIndex(result);
            var shortcuts = _entry.Shortcuts;
            if (index >= 0 && index < shortcuts.Count && !_fields.ContainsKey(shortcuts[index]))
            {
                field.Shortcut = shortcuts[index];
                _fields[shortcuts[index]] = field;
            }

            // a new shortcut that was canceled (or recorded a gesture the action already had)
            // leaves. With the focus still on it — Esc, or the no-op — it leaves now and hands the
            // focus to the add button, as a removed field does, rather than dropping it to the top
            // of the window; with the focus already moved elsewhere by a click or Tab, the user's
            // press is still in progress and its slot is held until it is over.
            var leaving = index < 0;
            var hadFocus = field.IsKeyboardFocusWithin;
            if (leaving && !hadFocus && _subscribed && HoldCanceledSlot(field))
                return;

            // a recording canceled because the page is leaving the window ends inside the field's
            // own detach, while Avalonia is still walking this panel's children: changing them
            // then is not safe, and the next attach refreshes anyway.
            if (_subscribed)
                Refresh();
            if (leaving && hadFocus && _subscribed)
                _addButton.Focus();
        }

        /// <summary>
        /// Keeps a canceled new shortcut's field in the panel, blank and inert, until the pointer
        /// press or key press that took the focus from it ends. The focus moves on the press, but
        /// a button acts on the release, and only if the pointer is still over it then — taking the
        /// slot away in between would move every control under it (the add button, the rows
        /// below) up by a field's height, out from under the pointer. False when there is no
        /// window to watch, and the field simply leaves.
        /// </summary>
        private bool HoldCanceledSlot(GestureField field)
        {
            var root = TopLevel.GetTopLevel(this);
            if (root == null)
                return false;

            _canceledSlot = field;
            _canceledSlotRoot = root;
            field.IsHitTestVisible = false;
            // nor reachable with Tab: a held Tab auto-repeats with no key-up between, and the focus
            // must not land on a field that is about to go.
            KeyboardNavigation.SetTabNavigation(field, KeyboardNavigationMode.None);

            // handledEventsToo, and on the window rather than the control pressed: whatever was
            // clicked, its own release handling (a button's Click) runs before this bubbles up, so
            // it has already acted against the layout it was pressed in. A focus moved by Tab has
            // no pointer release, so the key-up ends the hold too.
            root.AddHandler(PointerReleasedEvent, OnCanceledSlotInputEnded, RoutingStrategies.Bubble, handledEventsToo: true);
            root.AddHandler(KeyUpEvent, OnCanceledSlotInputEnded, RoutingStrategies.Bubble, handledEventsToo: true);
            return true;
        }

        private void OnCanceledSlotInputEnded(object sender, RoutedEventArgs e)
        {
            if (_canceledSlot == null)
                return;

            ReleaseCanceledSlot();
            if (_subscribed)
                Refresh();
        }

        /// <summary>Stops holding the canceled slot (see <see cref="HoldCanceledSlot"/>); the caller
        /// refreshes, which takes it out of the panel.</summary>
        private void ReleaseCanceledSlot()
        {
            if (_canceledSlotRoot != null)
            {
                _canceledSlotRoot.RemoveHandler(PointerReleasedEvent, OnCanceledSlotInputEnded);
                _canceledSlotRoot.RemoveHandler(KeyUpEvent, OnCanceledSlotInputEnded);
            }

            _canceledSlot = null;
            _canceledSlotRoot = null;
        }

        /// <summary>Assigns the action's gestures (saving them and re-registering) without
        /// refreshing midway; the caller refreshes once its own bookkeeping is done.</summary>
        private void Apply(GestureList gestures)
        {
            _applying = true;
            try
            {
                _entry.Gestures = gestures;
            }
            finally
            {
                _applying = false;
            }
        }

        private int IndexOfShortcut(HotkeyShortcut shortcut)
        {
            var shortcuts = _entry.Shortcuts;
            for (int i = 0; i < shortcuts.Count; i++)
            {
                if (ReferenceEquals(shortcuts[i], shortcut))
                    return i;
            }

            return -1;
        }
    }
}
