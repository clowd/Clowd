using System;

namespace Clowd.Config
{
    /// <summary>
    /// Greys out a settings row in the generated settings UI while another bool property on the
    /// same object is true — for a setting that is only meaningful when some other option is off
    /// (the manual accent colour, disabled while the OS accent is being followed). The row stays
    /// visible so the user can see what the value would be.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class DisabledWhenAttribute : Attribute
    {
        /// <summary>Name of a bool property on the same settings object.</summary>
        public string PropertyName { get; }

        public DisabledWhenAttribute(string propertyName)
        {
            PropertyName = propertyName;
        }
    }
}
