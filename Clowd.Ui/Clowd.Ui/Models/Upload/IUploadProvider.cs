using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Ui.Models.Upload;

public interface IUploadProvider
{
    string Name { get; }
    string Description { get; }
    SupportedUploadType SupportedUpload { get; }

    Task<string> UploadAsync(Stream content, string fileName, CancellationToken ct);
}
