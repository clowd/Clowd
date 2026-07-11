using System.Text.Json.Serialization;

namespace Clowd.Util;

[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SendArgsRequestModel))]
[JsonSerializable(typeof(Clowd.UI.Pages.CreditsData))]
internal partial class ClowdUiJsonContext : JsonSerializerContext { }
