using System.Text.Json.Serialization;

namespace Prisc.Util;

[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SendArgsRequestModel))]
[JsonSerializable(typeof(Prisc.UI.Pages.CreditsData))]
[JsonSerializable(typeof(Prisc.UI.ObsSettingsJson))]
[JsonSerializable(typeof(Prisc.UI.ScrollDriverEvent))]
[JsonSerializable(typeof(Prisc.UI.ScrollDriverCommand))]
internal partial class PriscUiJsonContext : JsonSerializerContext { }
