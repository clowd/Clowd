using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Clowd.UI.Controls;

namespace Clowd.UI.Config
{
    /// <summary>
    /// One shortcut of a hotkey action: a gesture button (click it, then press the new
    /// combination), a remove button, and the gesture's live registration status (a colored dot
    /// and a word, with the registration error in a tooltip). Also the empty field a new shortcut
    /// is recorded into.
    /// </summary>
    /// <remarks>
    /// The field records; it does not decide what a recording means for the action's list. A click
    /// raises <see cref="EditRequested"/>, the owner starts the recording with
    /// <see cref="BeginListening"/>, and <see cref="ListeningEnded"/> hands it the result (null for
    /// a cancel) — see <see cref="HotkeyShortcutsEditor"/>, which turns that into list edits.
    /// <para>The combination is recorded by the global keyboard hook
    /// (<see cref="HotkeyManager.StartRecording"/>), not from this window's key events: the hook
    /// sees keys the window never gets (PrtScr, Win combinations), and registered hotkeys stay
    /// silent while it records, so the combination being pressed does not fire its action. The
    /// hook hears every app, so the recording never outlives the user's attention on this field:
    /// the focus leaving it or its window being deactivated cancels it, as do Esc on its own and
    /// the Windows task-switching combinations (<see cref="HotkeyGesture.IsSystemReserved"/>).
    /// Modifier keys on their own only update the preview. Win+L is recorded like any other
    /// combination, but Windows always locks on it as well — the hook can never suppress it.</para>
    /// <para>Without a working hook (macOS before Accessibility is granted, Linux, the library
    /// missing) nothing can be recorded, and the field says why in its status.</para>
    /// </remarks>
    public class GestureField : UserControl
    {
        /// <summary>The shortcut whose gesture and status the field shows while idle; null for a
        /// field recording a new shortcut. Ignored while listening.</summary>
        public HotkeyShortcut Shortcut
        {
            get => _shortcut;
            set
            {
                if (ReferenceEquals(_shortcut, value))
                    return;

                if (_shortcut != null && _subscribed)
                    _shortcut.PropertyChanged -= OnShortcutPropertyChanged;
                _shortcut = value;
                if (_shortcut != null && _subscribed)
                    _shortcut.PropertyChanged += OnShortcutPropertyChanged;
                UpdateControls();
            }
        }

        public bool IsListening { get; private set; }

        /// <summary>Why the last <see cref="BeginListening"/> could not record (no hook) or its
        /// recording ended without a gesture (the hook died), or null.</summary>
        public string RecordingError => _recordingError;

        /// <summary>The gesture button was clicked: the owner decides whether to start listening.</summary>
        public event EventHandler EditRequested;

        /// <summary>The remove button was clicked.</summary>
        public event EventHandler RemoveRequested;

        /// <summary>Listening is over: the recorded gesture, or null when it was canceled. The
        /// recording has already ended when this is raised.</summary>
        public event EventHandler<HotkeyGesture> ListeningEnded;

        private readonly string _actionName;
        private const string StatusActive = "Active";
        private const string StatusListening = "Listening";
        private const string StatusError = "Error";

        private readonly Button _button;
        private readonly Border _listenRing;
        private readonly Button _removeButton;
        private readonly Ellipse _statusDot;
        private readonly TextBlock _statusText;
        private readonly StackPanel _statusPanel;
        private HotkeyShortcut _shortcut;
        private bool _subscribed;
        private HotkeyModifiers _editModifiers;
        private IDisposable _session;
        private Window _window;
        private string _recordingError;

        /// <param name="actionName">The display name of the action the shortcut belongs to: a
        /// screen reader announces it with the gesture, since a row's several gesture buttons are
        /// otherwise told apart only by their key text.</param>
        public GestureField(string actionName)
        {
            _actionName = actionName;
            AddStatusStyles();

            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(6)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            _button = new Button
            {
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _button.Click += OnButtonClick;
            ToolTip.SetTip(_button, "Click, then press a new key combination");

            // drawn over the button rather than set as its border: Semi's button states restyle
            // the template's border on hover and press, which would blink the ring away while the
            // pointer is still over the field the user just clicked.
            _listenRing = new Border
            {
                BorderThickness = new Thickness(1.5),
                IsHitTestVisible = false,
                IsVisible = false,
            };
            _listenRing.Bind(Border.BorderBrushProperty, _listenRing.GetResourceObservable("SemiColorPrimary"));
            _listenRing.Bind(Border.CornerRadiusProperty, _button.GetObservable(TemplatedControl.CornerRadiusProperty));

            var buttonHost = new Panel();
            buttonHost.Children.Add(_button);
            buttonHost.Children.Add(_listenRing);
            // a preferred rather than fixed width, so a narrow window shrinks the field instead
            // of clipping the status beside it (PreferredWidthBox). Generous on purpose: at the
            // window's default size the field fills the row and the status ends where the
            // section's divider does; only a wide window stops it short.
            grid.Children.Add(new PreferredWidthBox(360, buttonHost));

            _removeButton = new Button
            {
                Content = "✕",
                Width = 28,
                Height = 32,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _removeButton.Classes.Add("Tertiary");
            _removeButton.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);
            ToolTip.SetTip(_removeButton, "Remove this shortcut");
            Grid.SetColumn(_removeButton, 2);
            grid.Children.Add(_removeButton);

            _statusDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _statusText = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _statusText.Bind(TextBlock.ForegroundProperty, _statusText.GetResourceObservable("SemiColorText2"));
            // the word sits over invisible copies of every status word, so the column is exactly as
            // wide as the longest one in this font: every field — and therefore every gesture
            // button — on the page ends up the same width, without a guessed pixel minimum.
            var statusWords = new Panel();
            foreach (var word in new[] { StatusActive, StatusListening, StatusError })
            {
                var sizer = new TextBlock { Text = word, FontSize = 12, Opacity = 0, IsHitTestVisible = false };
                AutomationProperties.SetAccessibilityView(sizer, AccessibilityView.Raw);
                statusWords.Children.Add(sizer);
            }
            statusWords.Children.Add(_statusText);

            _statusPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent, // hit-testable so the error tooltip shows
            };
            _statusPanel.Children.Add(_statusDot);
            _statusPanel.Children.Add(statusWords);
            Grid.SetColumn(_statusPanel, 4);
            grid.Children.Add(_statusPanel);

            Content = grid;
            // the field takes the focus only programmatically, while it records (BeginListening);
            // Tab should land on its gesture button instead of on an unnamed stop that ignores
            // Enter and Space.
            Focusable = true;
            IsTabStop = false;

            UpdateControls();
        }

        /// <summary>The dot's color per state, as theme resources so it follows light/dark.</summary>
        private void AddStatusStyles()
        {
            void Dot(string cls, string token) =>
                Styles.Add(new Style(x => x.OfType<Ellipse>().Class(cls))
                {
                    Setters = { new Setter(Shape.FillProperty, new DynamicResourceExtension(token)) },
                });

            Dot("active", "SemiColorSuccess");
            Dot("error", "SemiColorDanger");
            Dot("listening", "SemiColorWarning");
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // subscribed only while shown: shortcuts belong to the app-lifetime HotkeyManager,
            // and a settings window (with every field on it) is recreated each time it opens.
            if (!_subscribed)
            {
                _subscribed = true;
                if (_shortcut != null)
                    _shortcut.PropertyChanged += OnShortcutPropertyChanged;
            }

            UpdateControls();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_subscribed)
            {
                _subscribed = false;
                if (_shortcut != null)
                    _shortcut.PropertyChanged -= OnShortcutPropertyChanged;
            }

            // the window closed (or the page was switched) mid-recording: nothing will ever take
            // the focus from a field that is no longer shown, so cancel now — a recording left
            // open would swallow the next key pressed in any app.
            if (IsListening)
                FinishListening(null);
        }

        private void OnShortcutPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // the status can change long after the gesture was set (the hook failing to start on
            // macOS without the Accessibility permission, the standby capturer reporting back) —
            // keep the indicator live.
            if (Dispatcher.UIThread.CheckAccess())
                UpdateControls();
            else
                Dispatcher.UIThread.Post(UpdateControls);
        }

        private void OnButtonClick(object sender, RoutedEventArgs e)
        {
            if (!IsListening)
                EditRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Starts recording through the hook and takes the keyboard focus. False (and nothing
        /// changed) when the field's window is not the active one, recording is unavailable — the
        /// field then shows why — or another recording is in progress. The field must be in the
        /// visual tree.
        /// </summary>
        public bool BeginListening()
        {
            var manager = HotkeyManager.Current;
            if (IsListening || manager == null)
                return false;

            // the hook records from any app: only start while the user is looking at this window
            if (TopLevel.GetTopLevel(this) is not Window { IsActive: true } window)
                return false;

            var session = manager.StartRecording(OnRecordModifiers, OnRecorded, OnRecordingFailed, out var error);
            if (session == null)
            {
                if (error != null)
                {
                    _recordingError = error;
                    UpdateControls();
                }
                return false;
            }

            _recordingError = null;
            _session = session;
            _window = window;
            IsListening = true;
            _editModifiers = HotkeyModifiers.None;
            LostFocus += OnLostFocus;
            window.Deactivated += OnWindowDeactivated;
            UpdateControls();

            // a field that was only just added (a new shortcut) can be refused the focus until it
            // has been laid out; give it one layout pass, and cancel if it still cannot have it —
            // without the focus no focus loss would ever cancel it.
            if (!Focus())
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsListening && !IsFocused && !Focus())
                        FinishListening(null);
                }, DispatcherPriority.Loaded);
            }

            return true;
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            // the button's own focus loss (when Focus() moves focus to this control) bubbles
            // here too — only react when this control itself loses focus.
            if (!ReferenceEquals(e.Source, this))
                return;

            FinishListening(null);
        }

        // another app took the foreground (a click elsewhere, Alt+Tab): the next key is not meant
        // for this field
        private void OnWindowDeactivated(object sender, EventArgs e) => FinishListening(null);

        private void OnRecordModifiers(HotkeyModifiers modifiers)
        {
            _editModifiers = modifiers;
            UpdateControls();
        }

        private void OnRecorded(HotkeyGesture gesture) => FinishListening(gesture);

        // the hook died mid-recording: a cancel, with the field saying why instead of listening on
        private void OnRecordingFailed(string error)
        {
            _recordingError = error;
            FinishListening(null);
        }

        private void FinishListening(HotkeyGesture gesture)
        {
            if (!IsListening)
                return;

            try
            {
                IsListening = false;
                LostFocus -= OnLostFocus;
                if (_window != null)
                    _window.Deactivated -= OnWindowDeactivated;
                _window = null;
            }
            finally
            {
                _session?.Dispose();
                _session = null;
            }
            UpdateControls();

            // Esc on its own and a focus loss (null) are cancels — an aborted recording must not
            // destroy the binding it was replacing. So are the task-switching combinations: the
            // hook swallowed that one press, the next one switches normally, and binding one would
            // break task switching system-wide.
            var canceled = gesture == null
                           || gesture.Key == HotkeyKey.None
                           || (gesture.Key == HotkeyKey.Escape && gesture.Modifiers == HotkeyModifiers.None)
                           || HotkeyGesture.IsSystemReserved(gesture);
            ListeningEnded?.Invoke(this, canceled ? null : gesture);
        }

        private void SetStatus(string dotClass, string text, string tooltip)
        {
            _statusDot.Classes.Set("active", dotClass == "active");
            _statusDot.Classes.Set("error", dotClass == "error");
            _statusDot.Classes.Set("listening", dotClass == "listening");
            _statusText.Text = text;
            ToolTip.SetTip(_statusPanel, tooltip);
        }

        /// <summary>The remove button always keeps its 28px column, so every field on the page ends
        /// at the same right edge; when it does not apply (a recording) it is only hidden.</summary>
        private void SetRemoveVisible(bool visible)
        {
            _removeButton.Opacity = visible ? 1 : 0;
            _removeButton.IsHitTestVisible = visible;
            _removeButton.Focusable = visible;
        }

        private void UpdateControls()
        {
            if (_button == null || _statusDot == null)
                return;

            _listenRing.IsVisible = IsListening;

            if (IsListening)
            {
                SetStatus("listening", StatusListening, null);
                SetRemoveVisible(false);

                // the modifiers held so far, so the user can see the combination being built
                var held = HotkeyGesture.ModifiersText(_editModifiers);
                _button.Content = held.Length == 0 ? "Press a key combination…" : held + " …";
                AutomationProperties.SetName(_button, _actionName + ": press a key combination");
                AutomationProperties.SetHelpText(_button, null);
                return;
            }

            var shortcut = _shortcut;
            if (shortcut == null)
            {
                // only briefly: a new shortcut's field between its recording and its removal (or
                // the slot it keeps until the click that canceled it is over)
                _button.Content = "";
                SetRemoveVisible(false);
                SetStatus(null, "", null);
                AutomationProperties.SetName(_button, null);
                AutomationProperties.SetHelpText(_button, null);
                ShowRecordingError();
                return;
            }

            // the gesture text is kept visible even on error — the red dot + tooltip carry the
            // registration failure.
            var text = shortcut.Gesture.ToString();
            _button.Content = text;
            SetRemoveVisible(true);
            AutomationProperties.SetName(_button, _actionName + " shortcut " + text);
            AutomationProperties.SetName(_removeButton, "Remove shortcut " + text + " from " + _actionName);
            // the status is otherwise only the dot, the word beside it and the error tooltip, none
            // of which the keyboard reaches
            if (!shortcut.IsRegistered && !String.IsNullOrEmpty(shortcut.Error))
            {
                // the word is kept short for the column; the reason is in the tooltip
                SetStatus("error", StatusError, shortcut.Error);
                AutomationProperties.SetHelpText(_button, "Not registered: " + shortcut.Error);
            }
            else
            {
                SetStatus("active", StatusActive, null);
                AutomationProperties.SetHelpText(_button, "Active");
            }

            ShowRecordingError();
        }

        /// <summary>Recording is unavailable (no hook): said where a registration error shows, so
        /// a click that cannot record is never silent.</summary>
        private void ShowRecordingError()
        {
            if (_recordingError == null)
                return;

            SetStatus("error", StatusError, _recordingError);
            AutomationProperties.SetHelpText(_button, "Cannot record a shortcut: " + _recordingError);
        }
    }
}
