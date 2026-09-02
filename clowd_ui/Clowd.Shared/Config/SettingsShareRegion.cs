using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Clowd.Config
{
    /// <summary>
    /// How the shared region is obscured when the HIDE tile is on. Mirrors the three non-none modes
    /// clowd_share_region's <c>obscure</c> command accepts; <c>none</c> is deliberately absent —
    /// that state is the toolbar tile being off, not a configuration. Mapped to
    /// <c>Clowd.UI.ShareObscureMode</c> inside Clowd.Ui: the protocol enum lives in the Clowd.Ui
    /// assembly and the project reference is one-way, so this cannot reuse it.
    /// Declaration order is dropdown order, so the default goes first.
    /// </summary>
    public enum ShareRegionObscureStyle
    {
        [Description("Blur")] Blur,
        [Description("Pixelate")] Pixelate,
        [Description("Hide (black card)")] Hide,
    }

    /// <summary>
    /// Settings for a shared region — the live mirror <c>clowd_share_region</c> puts on screen for a
    /// meeting app to share.
    /// <para>Its own settings class, and its own page in the main window, rather than a section of
    /// <see cref="SettingsCapture"/>: a share is not a capture, nothing here is a property of the
    /// selection overlay, and the one setting that IS —
    /// <see cref="SettingsCapture.ShareRegionEnabled"/>, which shows or hides the overlay's SHARE
    /// button — stays there with the other overlay buttons.</para>
    /// <para>Every value is read by <c>ShareRegionPage</c>. The obscure pair are live stdin
    /// commands and reach a running share; <see cref="Fps"/> is a spawn-time argument and reaches
    /// only the next one, which its own description says out loud.</para>
    /// </summary>
    public class SettingsShareRegion : SimpleNotifyObject
    {
        /// <summary>
        /// Canvas frame rate of the mirror window, handed to <c>clowd_share_region --fps</c>.
        /// <para>Unlike the obscure settings below it this one is read at SPAWN time, because the
        /// frame rate is a property of the OBS canvas the helper builds during bootstrap and there
        /// is no stdin command to change it. Editing it therefore affects the NEXT shared region,
        /// not one already running — the helper can never be respawned for a live share (the
        /// one-HWND invariant: the meeting app's share is bound to the mirror window's HWND, so a
        /// second process is a window nobody is watching). The description says as much, because a
        /// setting whose effect is deferred and does not say so reads as one that does nothing.</para>
        /// </summary>
        [Category("Video")]
        [DisplayName("Frame rate")]
        [Description("Frame rate of the shared region's mirror window. Lower costs less GPU and less " +
                     "of the meeting's bandwidth; higher is smoother for motion and video. Applies to " +
                     "the next region you share, not one already being shared.")]
        [Range(1, 240)]
        public int Fps
        {
            get => _fps;
            set => Set(ref _fps, value);
        }

        [Category("Obscure")]
        [DisplayName("Mode")]
        [Description("How the shared region is obscured while the HIDE button is on. Hide covers it " +
                     "completely; blur and pixelate leave shapes and movement visible. If the sharing " +
                     "helper's GPU effect fails to build, obscuring is unavailable for that session " +
                     "whatever this says.")]
        public ShareRegionObscureStyle ObscureStyle
        {
            get => _obscureStyle;
            // The derived gate raises nothing on its own — SimpleNotifyObject.Set only notifies the
            // property it wrote — so it is passed as a dependent. That costs the [CallerMemberName],
            // hence the explicit first argument.
            set => Set(ref _obscureStyle, value, nameof(ObscureStyle), nameof(ObscureUsesStrength));
        }

        [Category("Obscure")]
        [DisplayName("Strength")]
        [Description("How strong the blur or pixelation is (1-100). Not used by Hide. Pixelate blocks " +
                     "stay small even at 100, so it obscures detail rather than censoring content.")]
        [Range(1, 100)]
        [DisabledWhen(nameof(ObscureUsesStrength), false)]
        public int ObscureStrength
        {
            get => _obscureStrength;
            set => Set(ref _obscureStrength, value);
        }

        /// <summary>Gate for the strength row. [DisabledWhen] is bool-only — it builds a
        /// FuncValueConverter&lt;bool,bool&gt; — so pointing it at the enum would compile and then
        /// silently never disable anything. Not [Browsable], so it is not a row of its own.</summary>
        [Browsable(false)]
        public bool ObscureUsesStrength => _obscureStyle != ShareRegionObscureStyle.Hide;

        private int _fps = 30;
        private ShareRegionObscureStyle _obscureStyle = ShareRegionObscureStyle.Blur;
        private int _obscureStrength = 75;
    }
}
