using System.Text.Json.Serialization;

namespace Clowd.VideoSDK;

/// <summary>Source-generated (de)serialization for the vid-render argument file — the same
/// pattern as Clowd.Ui's ClowdUiJsonContext and Clowd.Upload's UploadJsonContext. Public because
/// Clowd.Ui writes the file and the tests read it back.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RenderArgs))]
public partial class RenderArgsJsonContext : JsonSerializerContext { }
