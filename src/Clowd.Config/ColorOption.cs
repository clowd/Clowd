namespace Clowd.Config;

public class ColorOption
{
    public byte A { get; set; }

    public byte R { get; set; }

    public byte G { get; set; }

    public byte B { get; set; }

    public ColorOption()
    {
    }

    public ColorOption(byte r, byte g, byte b)
    {
        R = r; G = g; B = b;
    }

    public ColorOption(byte r, byte g, byte b, byte a)
    {
        R = r; G = g; B = b; A = a;
    }
}
