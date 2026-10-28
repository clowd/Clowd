using Avalonia.Media;
using Prisc.UI.Controls.Tray;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The engaged frame's glow and the glow burst (<see cref="TrayTokens.EngagedGlow"/>,
    /// <see cref="TrayTokens.BurstGlow"/>) must fade out inside the room the window reserves for them, or
    /// the window edge cuts the glow off in a hard line.
    /// </summary>
    public class TrayEngagedGlowTests
    {
        [Fact]
        public void Glows_fade_out_inside_the_window_reserve()
        {
            foreach (var glow in new[] { TrayTokens.EngagedGlow(Colors.DodgerBlue), TrayTokens.BurstGlow(Colors.DodgerBlue) })
            {
                foreach (var shadow in glow)
                    Assert.True(Reach(shadow) <= TrayTokens.GlowReserve, $"{shadow} reaches {Reach(shadow)}");
            }
        }

        [Fact]
        public void Glows_are_drawn_in_the_accent()
        {
            foreach (var shadow in TrayTokens.EngagedGlow(Colors.DodgerBlue))
            {
                var accent = Colors.DodgerBlue;
                Assert.Equal(Color.FromArgb(shadow.Color.A, accent.R, accent.G, accent.B), shadow.Color);
            }
        }

        /// <summary>How far a shadow reaches past the tray edge: its spread plus three of the Gaussian's
        /// sigmas, by Avalonia's blur-radius-to-sigma rule (0.288675·r + 0.5), past which it is under half
        /// a percent of its strength.</summary>
        private static double Reach(BoxShadow shadow) =>
            shadow.Spread + (shadow.Blur > 0 ? 3 * (0.288675 * shadow.Blur + 0.5) : 0);
    }
}
