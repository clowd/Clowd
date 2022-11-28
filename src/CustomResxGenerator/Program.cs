

using System.Xml.Linq;
using System.Xml;
using System.Text.RegularExpressions;
using System.Text;

try
{
    if (args.Length != 3)
        throw new Exception("Invalid number of arguments: " + args.Length);

    string name = args[0];
    string resxFile = args[1];
    string output = args[2];

    if (!File.Exists(resxFile))
        throw new Exception("input file must exist");

    //var filename = name + ".custom.g.cs";

    StringBuilder sb = new StringBuilder();

    sb.AppendLine("using System;");
    //sb.AppendLine("using System.Globalization;");
    //sb.AppendLine("using System.Resources;");

    sb.AppendLine();
    sb.AppendLine("namespace Clowd.Localization;");
    
    sb.AppendLine();
    sb.AppendLine($"public enum {name}Keys");
    sb.AppendLine("{");

    var nonPlural = from l in ReadResxFile(resxFile)
                    let key = l.key
                    where !key.EndsWith("_Zero", StringComparison.InvariantCulture)
                    where !key.EndsWith("_One", StringComparison.InvariantCulture)
                    where !key.EndsWith("_Other", StringComparison.InvariantCulture)
                    where !key.EndsWith("_Two", StringComparison.InvariantCulture)
                    where !key.EndsWith("_Few", StringComparison.InvariantCulture)
                    where !key.EndsWith("_Many", StringComparison.InvariantCulture)
                    select l;

    foreach (var line in nonPlural)
    {
        sb.AppendLine($"    {line.key},");
    }

    sb.AppendLine("}");

    sb.AppendLine();

    sb.AppendLine($"public enum {name}PluralKeys");
    sb.AppendLine("{");

    var yesPlural = from l in ReadResxFile(resxFile)
                    let key = l.key
                    where key.EndsWith("_Zero", StringComparison.InvariantCulture)
                       || key.EndsWith("_One", StringComparison.InvariantCulture)
                       || key.EndsWith("_Other", StringComparison.InvariantCulture)
                       || key.EndsWith("_Two", StringComparison.InvariantCulture)
                       || key.EndsWith("_Few", StringComparison.InvariantCulture)
                       || key.EndsWith("_Many", StringComparison.InvariantCulture)
                    let idx = key.IndexOf('_')
                    let trimmed = key.Substring(0, idx)
                    group l by trimmed into g
                    select new { key = g.Key, g.FirstOrDefault(z => z.key.EndsWith("Other")).value };

    foreach (var line in yesPlural)
    {
        sb.AppendLine($"    {line.key},");
    }

    sb.AppendLine("}");

    sb.AppendLine();

    sb.AppendLine($"partial class {name}");
    sb.AppendLine("{");

    foreach (var line in nonPlural)
    {
        var matches = Regex.Match(line.value, "{\\d+(?:[:,].*?)?}");
        if (matches.Success)
        {
            sb.Append($"    public static string {line.key}(");
            for (int i = 0; i < matches.Captures.Count; i++)
                sb.Append(i == 0 ? "object A0" : $", object A{i}");
            sb.Append($") => String.Format(GetString(nameof({line.key})), ");
            for (int i = 0; i < matches.Captures.Count; i++)
                sb.Append(i == 0 ? "A0" : $", A{i}");
            sb.AppendLine(");");
        }
        else
        {
            sb.AppendLine($"    public static string {line.key} => GetString(nameof({line.key}));");
        }
    }

    foreach (var line in yesPlural)
    {
        var matches = Regex.Match(line.value, "{\\d+(?:[:,].*?)?}");
        if (matches.Success && matches.Captures.Count > 1)
        {
            sb.Append($"    public static string {line.key}(");
            for (int i = 0; i < matches.Captures.Count; i++)
                sb.Append(i == 0 ? "double PV" : $", object A{i}");
            sb.Append($") => String.Format(GetPlural(nameof({line.key}), PV), ");
            for (int i = 1; i < matches.Captures.Count; i++)
                sb.Append(i == 1 ? "A1" : $", A{i}");
            sb.AppendLine(");");
        }
        else
        {
            sb.AppendLine($"    public static string {line.key}(double PV) => GetPlural(nameof({line.key}), PV);");
        }
    }

    sb.AppendLine("}");

    File.WriteAllText(output, sb.ToString());
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine(ex.ToString());
    return -1;
}

static IEnumerable<(string key, string value, IXmlLineInfo line)> ReadResxFile(string filePath)
{
    using var stream = File.OpenRead(filePath);
    if (XDocument.Load(stream, LoadOptions.SetLineInfo).Root is { } element)
        return element
            .Descendants()
            .Where(static data => data.Name == "data")
            .Select(static data => (
                key: data.Attribute("name")!.Value,
                value: data.Descendants("value").First().Value,
                line: (IXmlLineInfo)data.Attribute("name")!
            ));

    return null;
}
