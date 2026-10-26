using System;
using System.Linq;

namespace Clowd.Config
{
    /// <summary>
    /// Shows a settings row only while another property on the same object holds one of
    /// <see cref="Values"/>, and hides it — row, label and caption — otherwise. A section whose
    /// rows are all hidden disappears with them. The row's value is kept either way; only its
    /// presence on the page changes.
    /// <para>Unlike <see cref="DisabledWhenAttribute"/>, which dims a row that does not apply
    /// right now, this removes rows that make no sense at all under the current mode — the
    /// webcam device on a page whose recording mode has no webcam track, or every recording
    /// setting once recording itself is switched off.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class VisibleWhenAttribute : Attribute
    {
        /// <summary>Name of a property on the same settings object — or, when <see cref="Section"/>
        /// is set, on that section of <see cref="SettingsRoot"/>.</summary>
        public string PropertyName { get; }

        /// <summary>
        /// Name of the <see cref="SettingsRoot"/> property (e.g. <c>nameof(SettingsRoot.Uploads)</c>)
        /// holding the object <see cref="PropertyName"/> lives on, for a row gated on another page's
        /// choice: the upload hotkeys on the Hotkeys page follow the Uploads page's mode. Null (the
        /// default) means the row's own object.
        /// </summary>
        public string Section { get; set; }

        /// <summary>The values of <see cref="PropertyName"/> under which the row is shown.</summary>
        public object[] Values { get; }

        public VisibleWhenAttribute(string propertyName, params object[] values)
        {
            PropertyName = propertyName;
            Values = values ?? Array.Empty<object>();
        }

        /// <summary>Whether a row carrying this attribute is shown while the gating property
        /// holds <paramref name="value"/>.</summary>
        public bool Matches(object value) => Values.Any(v => Equals(v, value));
    }
}
