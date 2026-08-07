using System.Text.Json.Serialization;

namespace Clowd.Util;

[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SendArgsRequestModel))]
[JsonSerializable(typeof(Clowd.UI.Pages.CreditsData))]
[JsonSerializable(typeof(Clowd.UI.ObsSettingsJson))]
[JsonSerializable(typeof(Clowd.UI.CaptureShowCommand))]
internal partial class ClowdUiJsonContext : JsonSerializerContext { }
