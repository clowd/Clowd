using System.Text.Json.Serialization;

namespace Clowd.VideoSDK.Model;

/// <summary>Source-generated (de)serialization for the v2 project file. Indented because the file is
/// read by humans when an edit or render goes wrong; enums as strings and nulls omitted for the same
/// reason. The polymorphic
/// <see cref="ItemContent"/> hierarchy is covered by its own [JsonDerivedType] attributes.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Project))]
public partial class ProjectJsonContext : JsonSerializerContext { }
