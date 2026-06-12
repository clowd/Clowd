namespace Clowd.Config
{
    /// <summary>
    /// Video capture is out of scope for this migration. The empty category is kept so that
    /// existing settings files containing a Video element still deserialize cleanly.
    /// </summary>
    public class SettingsVideo : CategoryBase
    { }
}
