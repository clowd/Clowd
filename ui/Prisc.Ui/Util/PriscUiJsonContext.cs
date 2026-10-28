using System.Text.Json.Serialization;

namespace Prisc.Util;

[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SendArgsRequestModel))]
[JsonSerializable(typeof(Prisc.UI.Pages.CreditsData))]
[JsonSerializable(typeof(Prisc.UI.RecorderSettingsJson))]
internal partial class PriscUiJsonContext : JsonSerializerContext { }
