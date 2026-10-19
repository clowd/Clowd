using System;

namespace Clowd.Config
{
    /// <summary>
    /// How far one press of a numeric settings row's spinner (or one wheel notch over it) moves the
    /// value. Without it the generated spinner steps by 1, or by 0.05 across a range of 2 or less.
    /// The step is only the spinner's: a property that must stay on the step's grid snaps typed
    /// values itself.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class SpinnerStepAttribute : Attribute
    {
        public double Step { get; }

        public SpinnerStepAttribute(double step)
        {
            Step = step;
        }
    }
}
