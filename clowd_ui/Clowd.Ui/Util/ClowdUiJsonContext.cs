using System.Text.Json.Serialization;

namespace Clowd.Util;

[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SendArgsRequestModel))]
internal partial class ClowdUiJsonContext : JsonSerializerContext { }
