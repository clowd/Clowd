using System;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Maps a linear progress value in [0,1] through a <see cref="TransitionEasing"/> curve.
    /// Shared by the visual transitions (<see cref="TransitionMath"/>) and by audio volume
    /// ramps (the future AudioMixer) so a fade sounds the way it looks.
    /// </summary>
    public static class Easing
    {
        /// <summary>Applies <paramref name="easing"/> to <paramref name="t"/>. Input is clamped
        /// to [0,1]; output is always in [0,1] with f(0)=0 and f(1)=1 for every curve.</summary>
        public static double Apply(TransitionEasing easing, double t)
        {
            if (t <= 0)
                return 0;
            if (t >= 1)
                return 1;

            switch (easing)
            {
                case TransitionEasing.CubicIn:
                    return t * t * t;

                case TransitionEasing.CubicOut:
                {
                    double u = 1 - t;
                    return 1 - u * u * u;
                }

                case TransitionEasing.CubicInOut:
                {
                    if (t < 0.5)
                        return 4 * t * t * t;
                    double u = -2 * t + 2;
                    return 1 - u * u * u / 2;
                }

                default:
                    return t;
            }
        }
    }
}
