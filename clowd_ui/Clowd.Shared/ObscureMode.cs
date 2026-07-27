namespace Clowd
{
    /// <summary>
    /// How a region obscured by the Pixelate tool is rendered. Mosaic MUST stay 0: obscured shapes
    /// persisted before this enum existed carry no mode in their JSON and deserialize to default.
    /// </summary>
    public enum ObscureMode
    {
        Mosaic,
        Blur,
        Solid,
    };
}
