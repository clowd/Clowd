using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls.Tray;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// Hidden harness for the floating tray controls, launched as
    /// <c>clowd --tray-spike generic|glyphs|recording|share|scroll [--vertical] [--shadow spec|compact]
    /// [--state name] [--cycle] [--nochevron] [--region full] [--exit-after ms]</c>
    /// — see App.Startup, which short-circuits before the single-instance mutex exactly as the
    /// <c>--video-spike</c> harness does, so a spike process never forwards its args to (or is
    /// swallowed by) a resident Clowd.
    /// <para>
    /// It exists because the tray controls are otherwise only reachable through a real recorder, a
    /// real share driver or a real scroll capture: this shows every control and every state with
    /// nothing behind them, deterministically, from one command line. There is no controller window
    /// on purpose — a second window would activate (these strips must never take focus) and would
    /// not be scriptable; the state to show is chosen per launch and the measurements go to stdout,
    /// so most acceptance checks are text rather than eyeballing.
    /// </para>
    /// <para>
    /// <c>generic</c> and <c>glyphs</c> drive throwaway windows built here out of the generic controls;
    /// <c>recording</c>, <c>share</c> and <c>scroll</c> drive the three REAL strips through their public
    /// API only, standing in for the page that normally owns their state. Those three build their own
    /// chassis options, so <c>--shadow</c> reaches only the two generic modes and <c>--nochevron</c> only
    /// the recording strip; <c>--vertical</c> reaches only the size-to-content modes, the fixed-size
    /// scroll strip never rotating.
    /// </para>
    /// <para>
    /// The only production reference is App.Startup's TryHandleArgs hook; nothing else may depend on
    /// this file.
    /// </para>
    /// </summary>
    public static class TraySpike
    {
        // private: App.Startup calls TryHandleArgs, never the literal, and nothing else may take a
        // dependency on the harness's spelling.
        private const string ArgName = "--tray-spike";

        // one interval for every state walk in the harness: long enough to read a state and to
        // screenshot it between steps, short enough that a six-rung lap finishes in 15 s.
        private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(2.5);

        /// <summary>Returns true (and takes over startup) when args request a tray spike.</summary>
        public static bool TryHandleArgs(string[] args)
        {
            if (args == null)
                return false;

            int index = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (String.Equals(args[i], ArgName, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return false;

            var mode = index + 1 < args.Length ? args[index + 1] : null;
            var vertical = HasFlag(args, "--vertical");
            var shadow = String.Equals(ValueOf(args, "--shadow"), "spec", StringComparison.OrdinalIgnoreCase)
                ? TrayTokens.Shadow
                : TrayTokens.ShadowCompact;
            var exitAfterMs = IntValueOf(args, "--exit-after");
            var state = ValueOf(args, "--state");
            var cycle = HasFlag(args, "--cycle");

            FloatingTrayWindow window = null;

            // only the scroll strip is fixed-size, and a fixed-size strip is the only one that can
            // refuse to show at all: the cascade's last rung puts a size-to-content strip inside the
            // region rather than giving up.
            var fixedSize = false;

            if (String.Equals(mode, "generic", StringComparison.OrdinalIgnoreCase))
                window = new GenericTrayWindow(shadow, vertical);
            else if (String.Equals(mode, "glyphs", StringComparison.OrdinalIgnoreCase))
                window = new GlyphTrayWindow(shadow);
            else if (String.Equals(mode, "recording", StringComparison.OrdinalIgnoreCase))
                window = new RecordingSpike(state, cycle, HasFlag(args, "--nochevron")).Window;
            else if (String.Equals(mode, "share", StringComparison.OrdinalIgnoreCase))
                window = new ShareSpike(state, cycle).Window;
            else if (String.Equals(mode, "scroll", StringComparison.OrdinalIgnoreCase))
            {
                window = new ScrollSpike(state, cycle).Window;
                fixedSize = true;
            }

            if (window == null)
            {
                Console.WriteLine("usage: " + ArgName + " generic|glyphs|recording|share|scroll [--vertical]"
                    + " [--shadow spec|compact] [--state <name>] [--cycle] [--nochevron] [--region full]"
                    + " [--exit-after <ms>] [--snapshot <png>]");
                Console.Out.Flush();
                // a harness process with no window would hang: the desktop lifetime shuts down only
                // on the tray's Exit item, and this path never sets a tray up.
                Environment.Exit(1);
            }

            AttachTracing(window, exitAfterMs);

            var snapshot = ValueOf(args, "--snapshot");
            if (snapshot != null)
                ScheduleSnapshot(window, snapshot);

            var region = String.Equals(ValueOf(args, "--region"), "full", StringComparison.OrdinalIgnoreCase)
                ? FullScreenRegion(window)
                : SpikeRegion(window, vertical);

            // App.Startup's catch shows a modal NiceDialog and only then exits, so a spike that throws
            // in its first layout pass looks like a silent no-op to a script that redirects stdout —
            // print the exception where the script can see it, then let Startup handle it as before.
            try
            {
                if (!fixedSize)
                {
                    window.ShowNear(region);
                }
                else if (!window.TryShowNear(region))
                {
                    // the placement search found nothing outside the region, so the strip was never
                    // shown — exactly what happens to the real HUD when a capture covers the monitor
                    // (the scroll page then runs without it). Nothing is left to wait for: print the
                    // one word the acceptance check greps for and leave.
                    Console.WriteLine("refused");
                    Console.Out.Flush();
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Console.Error.Flush();
                throw;
            }

            return true;
        }

        /// <summary>
        /// The region the strip is shown against. Horizontal: 800x500 centred on the primary
        /// monitor, which leaves room below and therefore takes the cascade's first rung.
        /// <para>
        /// <c>--vertical</c> does NOT rotate the window by hand — the axis has exactly one writer
        /// (the chassis), so the spike asks for it the way the app does: a region tall enough to
        /// leave no room above or below forces the cascade onto its vertical rung, which also means
        /// the printed <c>orientation</c> line is evidence about the real placement code.
        /// </para>
        /// </summary>
        private static ScreenRect SpikeRegion(Window window, bool vertical)
        {
            var screen = DesktopScreens.Primary(window);
            var bounds = screen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
            var work = screen?.WorkingArea ?? bounds;

            if (vertical)
                return ScreenRect.FromLTRB(work.X + 40, work.Y + 8, work.X + 540, work.Y + work.Height - 8);

            const int w = 800, h = 500;
            return new ScreenRect(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
        }

        /// <summary>
        /// <c>--region full</c>: the whole primary monitor, which leaves no room outside the region at
        /// all. The only case a fixed-size strip cannot be placed for, and therefore the only way to
        /// exercise <see cref="FloatingTrayWindow.TryShowNear"/> returning false without a contrived
        /// capture. A size-to-content strip still has a rung left (inside the region), so the other
        /// modes place as usual against it.
        /// </summary>
        private static ScreenRect FullScreenRegion(Window window)
        {
            var bounds = DesktopScreens.Primary(window)?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
            return new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        /// <summary>
        /// Prints <c>tray WxH</c> (logical), <c>window X,Y WxH</c> (capture px) and
        /// <c>orientation …</c> whenever any of them changes: after the first layout, after every
        /// rotation, and after the visibility test below changes the strip's length. Reading the
        /// numbers off stdout is what keeps the size acceptance checks textual.
        /// <para>
        /// This only observes layout — it never repositions anything (placement has one owner, the
        /// chassis). <c>PositionChanged</c> is needed as well because the placement pass runs after
        /// the layout that triggered it.
        /// </para>
        /// </summary>
        private static void AttachTracing(FloatingTrayWindow window, int? exitAfterMs)
        {
            string last = null;

            void Print()
            {
                var tray = window.Tray.Bounds;
                if (tray.Width <= 0 || tray.Height <= 0)
                    return;

                var scaling = OperatingSystem.IsMacOS() ? 1.0 : window.RenderScaling;
                var size = window.Bounds.Size;
                if (size.Width <= 0 || size.Height <= 0)
                    size = window.ClientSize;

                var line = String.Format(CultureInfo.InvariantCulture,
                    "tray {0:0.##}x{1:0.##}{6}window {2},{3} {4}x{5}{6}orientation {7}",
                    tray.Width, tray.Height,
                    window.Position.X, window.Position.Y,
                    (int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling),
                    Environment.NewLine, window.Orientation);

                if (line == last)
                    return;

                last = line;
                Console.WriteLine(line);
                Console.Out.Flush();
            }

            window.LayoutUpdated += (s, e) => Print();
            window.PositionChanged += (s, e) => Print();

            if (exitAfterMs is > 0)
                DispatcherTimer.RunOnce(() =>
                {
                    Console.Out.Flush();
                    Environment.Exit(0);
                }, TimeSpan.FromMilliseconds(exitAfterMs.Value));
        }

        /// <summary>
        /// Renders the window's content — the tray AND the transparent shadow reserve around it — to a
        /// PNG through a RenderTargetBitmap, without a screenshot, then prints the shadow's alpha along a
        /// line running out of the tray on each side (one number per logical px, from the tray edge to
        /// the window edge). A non-zero last number means the reserve clips the shadow.
        /// </summary>
        private static void ScheduleSnapshot(FloatingTrayWindow window, string path)
        {
            DispatcherTimer.RunOnce(() =>
            {
                try
                {
                    var root = (Control)window.Content;
                    var scale = window.RenderScaling;
                    var size = window.ClientSize;
                    var pixels = new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
                    using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
                    bitmap.Render(root);
                    bitmap.Save(path, PngBitmapEncoderOptions.Default);

                    var stride = pixels.Width * 4;
                    var buffer = new byte[stride * pixels.Height];
                    var native = Marshal.AllocHGlobal(buffer.Length);
                    try
                    {
                        bitmap.CopyPixels(new PixelRect(pixels), native, buffer.Length, stride);
                        Marshal.Copy(native, buffer, 0, buffer.Length);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(native);
                    }

                    int Alpha(double x, double y)
                    {
                        var px = Math.Clamp((int)(x * scale), 0, pixels.Width - 1);
                        var py = Math.Clamp((int)(y * scale), 0, pixels.Height - 1);
                        return buffer[py * stride + px * 4 + 3];
                    }

                    var tray = window.Tray.Bounds;
                    var cx = tray.Center.X;
                    var cy = tray.Center.Y;
                    string Line(string side, Func<int, int> at, int count)
                    {
                        var parts = new string[count];
                        for (var i = 0; i < count; i++)
                            parts[i] = at(i).ToString(CultureInfo.InvariantCulture);
                        return side + " " + String.Join(",", parts);
                    }

                    Console.WriteLine("snapshot " + path);
                    Console.WriteLine(String.Create(CultureInfo.InvariantCulture, $"shadow {window.Tray.Shadow} margin {window.Tray.Margin}"));
                    Console.WriteLine(Line("shadow-bottom", i => Alpha(cx, tray.Bottom + i), (int)(size.Height - tray.Bottom)));
                    Console.WriteLine(Line("shadow-top", i => Alpha(cx, tray.Top - 1 - i), (int)tray.Top));
                    Console.WriteLine(Line("shadow-left", i => Alpha(tray.Left - 1 - i, cy), (int)tray.Left));
                    Console.WriteLine(Line("shadow-right", i => Alpha(tray.Right + i, cy), (int)(size.Width - tray.Right)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("snapshot failed: " + ex);
                }

                Console.Out.Flush();
            }, TimeSpan.FromMilliseconds(800));
        }

        /// <summary>
        /// A dispatcher timer that dies with its window. The three real strips are sealed, so a harness
        /// driver cannot stop its timers from <c>OnClosed</c> the way the two windows below do.
        /// </summary>
        private static void StartTimer(Window window, TimeSpan interval, Action tick)
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += (s, e) => tick();
            window.Closed += (s, e) => timer.Stop();
            timer.Start();
        }

        /// <summary>
        /// Leaves, the way Finish/Cancel end a real session. The harness has no session to end and no
        /// tray Exit item to shut the lifetime down, so a strip whose last button was pressed has to
        /// take the process with it or it sits there undismissable.
        /// </summary>
        private static void ExitSpike()
        {
            Console.Out.Flush();
            Environment.Exit(0);
        }

        /// <summary>An unknown <c>--state</c> says what was expected rather than showing some other state
        /// (a script that misspells one must not get a screenshot of the wrong thing).</summary>
        private static void UnknownState(string value, string valid)
        {
            Console.WriteLine("unknown --state '" + value + "'; expected " + valid);
            Console.Out.Flush();
            Environment.Exit(1);
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (var arg in args)
                if (String.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string ValueOf(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static int? IntValueOf(string[] args, string name)
            => Int32.TryParse(ValueOf(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (int?)null;

        /// <summary>
        /// Every generic control, in every state it has: the transport button walking its four
        /// states, the four shapes a split toggle can take, the five button looks, and (horizontal
        /// only) the status block.
        /// </summary>
        private sealed class GenericTrayWindow : FloatingTrayWindow
        {
            private static readonly TrayPrimaryState[] States =
            {
                TrayPrimaryState.Idle, TrayPrimaryState.Active, TrayPrimaryState.Paused, TrayPrimaryState.Waiting,
            };

            private readonly TrayPrimaryButton _primary;
            private readonly TraySplitToggle _sweeping;
            private readonly TrayButton _blinking;

            // the clock starts near the wrap on purpose: the acceptance check is that the widest label
            // the primary can ever show ("59:59", spec §4) still fits its fixed 60 px, and a clock that
            // started at 00:00 would only ever prove "00:01" fits. Active lasts 2.5 s, so the label
            // walks 59:57 → 59:59 and never reaches a sixth character.
            private static readonly TimeSpan ClockStart = TimeSpan.FromSeconds((59 * 60) + 57);

            private int _stateIndex;
            private double _phase;
            private TimeSpan _elapsed = ClockStart;

            public GenericTrayWindow(BoxShadows shadow, bool vertical)
                : base(new FloatingTrayOptions { Title = "Clowd Tray Spike", Shadow = shadow })
            {
                _primary = new TrayPrimaryButton { State = TrayPrimaryState.Idle, Label = "Start" };
                ToolTip.SetTip(_primary, "Transport · Idle");
                Tray.Items.Add(_primary);

                // on, with a chevron and a level that sweeps at 10 Hz — the only one whose bar moves
                _sweeping = AddToggle(TrayGlyphs.Mic, TrayGlyphs.MicOff, isOn: true, hasChevron: true, statusOnly: false, enabled: true, chevronEnabled: true);
                AddToggle(TrayGlyphs.Spk, TrayGlyphs.SpkOff, isOn: false, hasChevron: true, statusOnly: false, enabled: true, chevronEnabled: true);
                AddToggle(TrayGlyphs.Eye, TrayGlyphs.EyeOff, isOn: true, hasChevron: false, statusOnly: true, enabled: true, chevronEnabled: true);
                AddToggle(TrayGlyphs.Cam, TrayGlyphs.CamOff, isOn: true, hasChevron: true, statusOnly: false, enabled: false, chevronEnabled: true);

                Tray.Items.Add(NewButton(TrayGlyphs.Sliders, TrayButtonLook.Normal, false, "Normal"));
                Tray.Items.Add(NewButton(TrayGlyphs.Resize, TrayButtonLook.Normal, true, "Normal + IsActive (accent fill)"));
                Tray.Items.Add(NewButton(TrayGlyphs.X, TrayButtonLook.Quiet, false, "Quiet"));
                Tray.Items.Add(NewButton(TrayGlyphs.Stop, TrayButtonLook.Danger, false, "Danger"));

                var split = new TraySplitButton
                {
                    Glyph = TrayGlyphs.Check, Look = TrayButtonLook.Normal, MainToolTip = "Split · main", MainName = "Main",
                    SideGlyph = TrayGlyphs.X, SideToolTip = "Split · side", SideName = "Side",
                };
                split.MainClicked += (s, e) => ShowStatusBlip("Split · main clicked");
                split.SideClicked += (s, e) => ShowStatusBlip("Split · side clicked");
                Tray.Items.Add(split);

                // last in the row so the visibility test below shortens the strip from its free end,
                // leaving every other measurement in place while the printed length changes.
                _blinking = NewButton(TrayGlyphs.Stop, TrayButtonLook.DangerFilled, false, "DangerFilled (visibility test)");
                Tray.Items.Add(_blinking);

                // TrayStatusBlock is horizontal-only by design, and its 200 px would set the width of
                // a vertical tray (and stretch every button to match), hiding the two numbers the
                // vertical pass is measured on: 42 px short edge and 34 px buttons.
                if (!vertical)
                    Tray.Items.Add(new TrayStatusBlock
                    {
                        Width = 200,
                        PrimaryText = "Frame 22 · 8,180 px",
                        SecondaryText = "Paused — stop moving the mouse to resume",
                    });

                StartTimer(this, CycleInterval, NextState);
                StartTimer(this, TimeSpan.FromMilliseconds(100), SweepLevel);
                StartTimer(this, TimeSpan.FromSeconds(1), TickClock);
                StartTimer(this, TimeSpan.FromSeconds(4), ToggleBlinker);

                // the blip is the chassis's own popup; 3 s in so it lands after the strip has settled
                DispatcherTimer.RunOnce(() => ShowStatusBlip("Blip · gone after 6 s"), TimeSpan.FromSeconds(3));
            }

            private TraySplitToggle AddToggle(TrayGlyph on, TrayGlyph off, bool isOn, bool hasChevron, bool statusOnly, bool enabled, bool chevronEnabled)
            {
                var toggle = new TraySplitToggle
                {
                    OnGlyph = on,
                    OffGlyph = off,
                    IsOn = isOn,
                    HasChevron = hasChevron,
                    IsChevronEnabled = chevronEnabled,
                    IsStatusOnly = statusOnly,
                    IsEnabled = enabled,
                    ToggleToolTip = on.Name + (statusOnly ? " · status only" : "") + (enabled ? "" : " · locked"),
                    ChevronToolTip = "Choose " + on.Name,
                };

                // the control never flips itself — the owner does, which is what lets a real owner
                // open a menu instead, or keep an optimistic state.
                toggle.ToggleClicked += (s, e) => toggle.IsOn = !toggle.IsOn;
                toggle.ChevronClicked += (s, e) => ShowMenu(BuildMenu(toggle.OnGlyph.Name), toggle);

                Tray.Items.Add(toggle);
                return toggle;
            }

            private static TrayButton NewButton(TrayGlyph glyph, TrayButtonLook look, bool active, string tip)
            {
                var button = new TrayButton { Glyph = glyph, Look = look, IsActive = active };
                ToolTip.SetTip(button, tip);
                return button;
            }

            /// <summary>A header row plus three radio rows — enough to check the popup themes.</summary>
            private static MenuFlyout BuildMenu(string source)
            {
                var flyout = new MenuFlyout();
                // upper-cased here, not in the theme: the `.header` style carries the spec's 10.5 px and
                // letter spacing but Avalonia has no text-transform, so the caller supplies the caption
                // already uppercase (spec §5) — real owners must do the same.
                var header = new MenuItem { Header = source.ToUpperInvariant(), IsHitTestVisible = false, Focusable = false };
                header.Classes.Add("header");
                flyout.Items.Add(header);

                for (int i = 0; i < 3; i++)
                    flyout.Items.Add(new MenuItem
                    {
                        Header = "Device " + (char)('A' + i),
                        ToggleType = MenuItemToggleType.Radio,
                        GroupName = "traySpike" + source,
                        IsChecked = i == 0,
                    });

                return flyout;
            }

            private void NextState()
            {
                _stateIndex = (_stateIndex + 1) % States.Length;
                var state = States[_stateIndex];
                if (state == TrayPrimaryState.Idle)
                    _elapsed = ClockStart;

                _primary.State = state;
                _primary.Label = LabelFor(state);
                ToolTip.SetTip(_primary, "Transport · " + state);
            }

            private string LabelFor(TrayPrimaryState state) => state switch
            {
                TrayPrimaryState.Idle => "Start",
                TrayPrimaryState.Waiting => "Wait…",
                _ => RecordingSourceRules.FormatElapsed(_elapsed),
            };

            private void TickClock()
            {
                if (_primary.State != TrayPrimaryState.Active)
                    return;

                _elapsed += TimeSpan.FromSeconds(1);
                _primary.Label = LabelFor(_primary.State);
            }

            private void SweepLevel()
            {
                _phase += 0.35;
                _sweeping.Level = (Math.Sin(_phase) + 1) / 2;
            }

            /// <summary>
            /// Collapsing one item is the check for whether the tray's StackPanel spacing counts a
            /// collapsed child: the printed tray length must change by 34 + 4, not 34.
            /// </summary>
            private void ToggleBlinker()
            {
                _blinking.IsVisible = !_blinking.IsVisible;
                QueueReposition();
            }
        }

        /// <summary>Every glyph once, so the hand-converted path data can be checked by eye.</summary>
        private sealed class GlyphTrayWindow : FloatingTrayWindow
        {
            public GlyphTrayWindow(BoxShadows shadow)
                : base(new FloatingTrayOptions { Title = "Clowd Tray Spike · glyphs", Shadow = shadow })
            {
                foreach (var glyph in TrayGlyphs.All)
                {
                    var button = new TrayButton { Glyph = glyph, Look = TrayButtonLook.Normal };
                    ToolTip.SetTip(button, glyph.Name);
                    Tray.Items.Add(button);
                }
            }
        }

        /// <summary>
        /// The real recording strip with no recorder behind it. The harness plays the page: it answers
        /// the transport clicks, feeds the elapsed clock the status stream would carry, and sweeps both
        /// level meters. The strip's own model still reads this machine's real settings and devices —
        /// that is the point of driving the real type — so what the three source toggles look like
        /// depends on what is plugged in.
        /// </summary>
        private sealed class RecordingSpike
        {
            // the state walk this harness cycles. Idle is reached back from Active through SetRecordingState(false),
            // the reverse half nothing in the app exercises (a real session closes the window instead).
            private static readonly string[] Walk = { "waiting", "idle", "active", "paused", "active", "idle" };

            private readonly RecordingFloatingButtons _strip;

            // seeded to the state the strip constructs itself in (it opens Waiting, because the real
            // recorder is still being built at that point) — the harness's mirror has to start where
            // the strip starts or the first transition is swallowed as a no-op.
            private bool _waiting = true;
            private bool _recording, _paused;
            private double _phase;
            private TimeSpan _elapsed;
            private int _walkIndex;

            public RecordingSpike(string state, bool cycle, bool noChevron)
            {
                // App.Startup hands the harness its args before SetupSettings runs, so the settings
                // singleton the recording model mirrors is still null here. Load the real file rather
                // than planting a defaults instance: the model audits devices and writes the user's
                // capture toggles back (debounced), and defaults would flatten their settings the first
                // time that save fired.
                SettingsRoot.Current ??= SettingsService.Load();

                _strip = new RecordingFloatingButtons();

                // the strip raises intent and the page owns the state, so with no page behind it the
                // transport buttons would look dead. These are the transitions VideoCapturePage makes.
                _strip.StartClicked += (s, e) => Apply("active");
                _strip.PauseToggleClicked += (s, e) => Apply(_paused ? "active" : "paused");
                _strip.FinishClicked += (s, e) => ExitSpike();
                _strip.CancelClicked += (s, e) => ExitSpike();

                // the page would open the settings page (the camera tile outside Studio mode routes here
                // too); with no page, a blip is the only honest answer — it proves the event landed.
                _strip.SettingsClicked += (s, e) => _strip.ShowStatusBlip("Options");

                if (noChevron)
                    ClearChevrons();

                // --cycle drives the walk from its first rung, so it ignores --state rather than starting
                // mid-walk and printing a sequence no acceptance check names.
                var direct = cycle ? null : state?.ToLowerInvariant();
                if (direct is "active" or "paused")
                {
                    // Harness-only ordering, and the reason this is not a strip change: the strip's
                    // model enumerates cameras fire-and-forget from its constructor (~60 ms), while
                    // UpdateLocks stops evaluating chevrons for the whole recording (while recording, the
                    // chevrons and the camera slot are disabled rather than hidden and HasChevron is frozen,
                    // so the strip never changes width mid-recording).
                    // Applying "active"/"paused" synchronously right here froze the camera slot before
                    // the list landed (a null list counts as no devices), so a direct --state active
                    // printed a chevron-less 320x40 where the waiting -> idle -> active walk reaches
                    // 372x48. So show the idle strip first, wait for the same enumeration the strip
                    // awaits (its constructor started one before this line, so ours lands no earlier),
                    // and only then apply the state - one hop later still, so the strip's own
                    // continuation (RefreshCamerasAsync -> Changed -> UpdateLocks -> SetChevron) has run.
                    // The real page is unaffected: it builds the strip long before a recording starts.
                    Apply("idle");
                    _ = SeedCamerasThenApplyAsync(direct, noChevron);
                }
                else
                {
                    Apply(cycle ? Walk[0] : state ?? "idle");
                }

                if (cycle)
                    StartTimer(_strip, CycleInterval, Step);

                // 1 Hz elapsed and a 10 Hz level sweep — the rates the real status stream and the audio
                // level feed arrive at. The meters are fed in every state, not only while recording, so
                // the level pill is on a screenshot of an idle strip too.
                StartTimer(_strip, TimeSpan.FromSeconds(1), TickClock);
                StartTimer(_strip, TimeSpan.FromMilliseconds(100), SweepLevels);
            }

            public FloatingTrayWindow Window => _strip;

            private void Apply(string state)
            {
                switch (state?.ToLowerInvariant())
                {
                    case "waiting":
                        SetRecording(false);
                        SetWaiting(true);
                        break;
                    case "idle":
                        SetRecording(false);
                        SetWaiting(false);
                        break;
                    case "active":
                        SetWaiting(false);
                        SetRecording(true);
                        SetPaused(false);
                        break;
                    case "paused":
                        SetWaiting(false);
                        SetRecording(true);
                        SetPaused(true);
                        break;
                    default:
                        UnknownState(state, "waiting|idle|active|paused");
                        break;
                }
            }

            private void Step()
            {
                _walkIndex = (_walkIndex + 1) % Walk.Length;
                Apply(Walk[_walkIndex]);
            }

            /// <summary>
            /// Chevrons are derived from the real device count, so on a machine with two microphones
            /// the single-device look is otherwise unreachable. Look-only: the strip's UpdateLocks owns
            /// HasChevron and re-asserts the real value on the next settings change, which is exactly
            /// why <c>--nochevron</c> is a screenshot flag and not a mode - and why the deferred path
            /// in the constructor re-applies it after the strip's own chevron pass.
            /// </summary>
            private void ClearChevrons()
            {
                foreach (var item in _strip.Tray.Items)
                    if (item is TraySplitToggle toggle)
                        toggle.HasChevron = false;
            }

            /// <summary>
            /// Harness-only: defers a direct <c>--state active|paused</c> until the camera list has
            /// landed, so the chevrons the recording state freezes are the real ones. See the
            /// constructor for why, and why nothing in the app needs this.
            /// </summary>
            private async Task SeedCamerasThenApplyAsync(string state, bool noChevron)
            {
                try
                {
                    // the manager's own enumeration, not the strip's (the harness has no handle on
                    // that one); CameraDeviceManager never throws, so this guards scheduling only.
                    await CameraDeviceManager.RefreshAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    Console.Error.Flush();
                }

                // Background priority is the extra hop: the strip's continuation of the enumeration it
                // started first is already queued at normal priority, so its SetChevron/QueueReposition
                // run before the recording state freezes them.
                Dispatcher.UIThread.Post(() =>
                {
                    if (noChevron)
                        ClearChevrons();

                    Apply(state);
                }, DispatcherPriority.Background);
            }

            // Each setter below is a no-op when nothing changes: the walk revisits states, and the
            // strip's methods are written for a page that calls them on real transitions only
            // (SetWaiting(false) in the middle of a recording says nothing about anything).
            private void SetWaiting(bool waiting)
            {
                if (_waiting == waiting)
                    return;

                _waiting = waiting;
                _strip.SetWaiting(waiting);
            }

            private void SetRecording(bool recording)
            {
                if (_recording == recording)
                    return;

                _recording = recording;
                if (recording)
                {
                    _elapsed = TimeSpan.Zero;
                }
                else
                {
                    // the strip's own SetRecordingState(false) unwinds the paused look, so mirror that
                    // here or the next Apply("paused") reads as a no-op.
                    _paused = false;
                }

                _strip.SetRecordingState(recording);
            }

            private void SetPaused(bool paused)
            {
                if (_paused == paused)
                    return;

                _paused = paused;
                _strip.SetPausedState(paused);
            }

            private void TickClock()
            {
                // frozen while paused, because the real status stream stops arriving there
                if (!_recording || _paused)
                    return;

                _elapsed += TimeSpan.FromSeconds(1);
                _strip.SetElapsed(_elapsed);
            }

            private void SweepLevels()
            {
                _phase += 0.35;

                // the speaker runs half a cycle behind the microphone so the two bars are never the same
                // length in a screenshot.
                _strip.SetAudioLevels(Dbfs(_phase), Dbfs(_phase + Math.PI));
            }

            /// <summary>A sine over −60..0 dBFS, the range TrayLevel maps onto the level pill.</summary>
            private static double Dbfs(double phase) => -60 + (60 * (Math.Sin(phase) + 1) / 2);
        }

        /// <summary>
        /// The real share strip with no share session behind it. Only the resize tile needs an owner's
        /// answer — it deliberately does not latch itself — so without a stand-in page a click on it
        /// would do nothing at all, while the hide tile flips optimistically on its own.
        /// </summary>
        private sealed class ShareSpike
        {
            // the state walk this harness cycles. Retirement is one-way by design, so the lap that reaches it never leaves:
            // the second lap walks visible/hidden/resize/busy with a retired tile, which is itself the
            // check that RetireHide survives a later SetResizeState(false, false).
            private static readonly string[] Walk = { "visible", "hidden", "resize", "busy", "visible", "retired" };

            private readonly ShareRegionFloatingButtons _strip;
            private int _walkIndex;

            public ShareSpike(string state, bool cycle)
            {
                _strip = new ShareRegionFloatingButtons();

                // the page is the authority on resize mode (entering it can be refused), so the harness
                // answers the request the way ShareRegionPage does — never busy, since there is no helper
                // here to wait for an applied rectangle from.
                _strip.ResizeToggled += (s, active) => _strip.SetResizeState(active, false);

                _strip.CancelClicked += (s, e) => ExitSpike();
                _strip.SettingsClicked += (s, e) => _strip.ShowStatusBlip("Options");

                Apply(cycle ? Walk[0] : state ?? "visible");

                if (cycle)
                    StartTimer(_strip, CycleInterval, Step);
            }

            public FloatingTrayWindow Window => _strip;

            private void Apply(string state)
            {
                switch (state?.ToLowerInvariant())
                {
                    case "visible":
                        _strip.SetResizeState(false, false);
                        _strip.SetHidden(false);
                        break;
                    case "hidden":
                        _strip.SetResizeState(false, false);
                        _strip.SetHidden(true);
                        break;
                    case "resize":
                        // the region is obscured for the duration of the mode, so the page never leaves
                        // the hide tile reading "hidden" underneath it
                        _strip.SetHidden(false);
                        _strip.SetResizeState(true, false);
                        break;
                    case "busy":
                        // a move in flight: what ShareRegionPage sets while it waits to hear which
                        // rectangle the helper actually applied
                        _strip.SetHidden(false);
                        _strip.SetResizeState(true, true);
                        break;
                    case "retired":
                        _strip.SetResizeState(false, false);
                        _strip.RetireHide();
                        break;
                    default:
                        UnknownState(state, "visible|hidden|resize|busy|retired");
                        break;
                }
            }

            private void Step()
            {
                _walkIndex = (_walkIndex + 1) % Walk.Length;
                Apply(Walk[_walkIndex]);
            }
        }

        /// <summary>
        /// The real scrolling HUD with no driver behind it: the four readouts ScrollCapturePage can show,
        /// including the widest secondary line <c>ScrollStatusStrip.StatusWidth</c> is sized for.
        /// </summary>
        private sealed class ScrollSpike
        {
            private static readonly string[] Walk = { "starting", "scrolling", "paused", "finishing" };

            private readonly ScrollStatusStrip _strip;
            private int _walkIndex;

            public ScrollSpike(string state, bool cycle)
            {
                _strip = new ScrollStatusStrip();

                // what the page says on Finish before it stops the driver; Cancel closes the page, which
                // here means leaving.
                _strip.FinishClicked += (s, e) => Apply("finishing");
                _strip.CancelClicked += (s, e) => ExitSpike();

                Apply(cycle ? Walk[0] : state ?? "starting");

                if (cycle)
                    StartTimer(_strip, CycleInterval, Step);
            }

            public FloatingTrayWindow Window => _strip;

            private void Apply(string state)
            {
                switch (state?.ToLowerInvariant())
                {
                    case "starting":
                        // the strip's own seed, restated so the walk can come back to it
                        _strip.SetStatus("Starting…", "Esc or Finish to stop");
                        break;
                    case "scrolling":
                        _strip.SetStatus("Frame 22 · 8,180 px", "Scrolling…");
                        break;
                    case "paused":
                        // the line the status column's width was chosen for (spec §8)
                        _strip.SetStatus("Frame 22 · 8,180 px", "Paused — stop moving the mouse to resume");
                        break;
                    case "finishing":
                        _strip.SetStatus("Finishing…", "Stitching the last frames");
                        break;
                    default:
                        UnknownState(state, "starting|scrolling|paused|finishing");
                        break;
                }
            }

            private void Step()
            {
                _walkIndex = (_walkIndex + 1) % Walk.Length;
                Apply(Walk[_walkIndex]);
            }
        }
    }
}
