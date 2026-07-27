using System.Text.Json.Serialization;

namespace Clowd.Util;

[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SendArgsRequestModel))]
[JsonSerializable(typeof(Clowd.UI.Pages.CreditsData))]
[JsonSerializable(typeof(Clowd.UI.ObsSettingsJson))]
internal partial class ClowdUiJsonContext : JsonSerializerContext { }
