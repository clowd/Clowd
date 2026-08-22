using System;

namespace Clowd.Config
{
    /// <summary>
    /// Grays out a settings row in the generated settings UI while another bool property on the
    /// same object holds <see cref="DisablingValue"/> — for a setting that is only meaningful when
    /// some other option is off (the manual accent color, disabled while the OS accent is being
    /// followed) or on (the scrolling-capture rewind, dead while scrolling capture itself is
    /// switched off). The row stays visible so the user can see what the value would be.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class DisabledWhenAttribute : Attribute
    {
        /// <summary>Name of a bool property on the same settings object.</summary>
        public string PropertyName { get; }

        /// <summary>The value of <see cref="PropertyName"/> that disables this row. Defaults to
        /// true ("disabled while that option is on"); pass false for a row that only makes sense
        /// while the other option is enabled.</summary>
        public bool DisablingValue { get; }

        public DisabledWhenAttribute(string propertyName, bool disablingValue = true)
        {
            PropertyName = propertyName;
            DisablingValue = disablingValue;
        }
    }
}
