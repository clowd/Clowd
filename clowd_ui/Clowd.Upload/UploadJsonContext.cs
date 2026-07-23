using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Clowd.Upload;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VgyResponse))]
[JsonSerializable(typeof(PicsurResponse))]
[JsonSerializable(typeof(ImgurApiResponse))]
[JsonSerializable(typeof(HasebinResponse))]
[JsonSerializable(typeof(MimeDbMimeEntry))]
[JsonSerializable(typeof(Dictionary<string, MimeDbMimeEntry>))]
internal partial class UploadJsonContext : JsonSerializerContext { }
