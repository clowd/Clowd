using System.Globalization;
using System.Xml.Linq;
using RT.Serialization;

namespace Clowd.Config;

/// <summary>
/// Enables <see cref="Classify"/> to save color properties as strings of a human-editable form.
/// </summary>
public sealed class ClassifyColorTypeOptions : IClassifyXmlTypeProcessor, IClassifySubstitute<ColorOption, string>
{
    public void AfterDeserialize(object obj, XElement element)
    { }

    public void AfterSerialize(object obj, XElement element)
    { }

    public void BeforeDeserialize(XElement element)
    {
        ColorOption dummy;
        if (!fromSubstitute(element.Value, out dummy))
        {
            // then it's using the old, native format: deserialize that and replace the value in the xml (to be processed via FromSubstitute later)
            var color = ClassifyXml.Deserialize<ColorOption>(element, new ClassifyOptions());
            element.Value = ToSubstitute(color);
        }
    }

    public void BeforeSerialize(object obj)
    { }

    private bool fromSubstitute(string instance, out ColorOption color)
    {
        color = new ColorOption();
        try
        {
            if (!instance.StartsWith("#") || (instance.Length != 7 && instance.Length != 9))
                return false;
            int alpha = instance.Length == 7 ? 255 : int.Parse(instance.Substring(1, 2), NumberStyles.HexNumber);
            int r = int.Parse(instance.Substring(instance.Length == 7 ? 1 : 3, 2), NumberStyles.HexNumber);
            int g = int.Parse(instance.Substring(instance.Length == 7 ? 3 : 5, 2), NumberStyles.HexNumber);
            int b = int.Parse(instance.Substring(instance.Length == 7 ? 5 : 7, 2), NumberStyles.HexNumber);
            color = new ColorOption((byte)r, (byte)g, (byte)b, (byte)alpha);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public ColorOption FromSubstitute(string instance)
    {
        ColorOption result;
        fromSubstitute(instance, out result);
        return result;
    }

    public string ToSubstitute(ColorOption instance)
    {
        return instance.A == 255
            ? string.Format("#{0:X2}{1:X2}{2:X2}", instance.R, instance.G, instance.B)
            : string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", instance.A, instance.R, instance.G, instance.B);
    }
}
