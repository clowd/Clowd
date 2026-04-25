using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Ui.Models.Upload.Providers;

// Stub providers — surface in the upload settings page so the user can see what will eventually
// be available, but actually uploading throws NotImplementedException for now. Each one is a
// real class so SettingsRoot can register it explicitly.

public sealed class ImgurUploadProvider : IUploadProvider
{
    public string Name => "Imgur";
    public string Description => "Free image hosting from imgur.com.";
    public SupportedUploadType SupportedUpload => SupportedUploadType.Image;
    public Task<string> UploadAsync(Stream content, string fileName, CancellationToken ct)
        => throw new NotImplementedException("Imgur provider not implemented yet.");
}

public sealed class HastebinUploadProvider : IUploadProvider
{
    public string Name => "Hastebin";
    public string Description => "Pastebin-style text snippet hosting.";
    public SupportedUploadType SupportedUpload => SupportedUploadType.Text;
    public Task<string> UploadAsync(Stream content, string fileName, CancellationToken ct)
        => throw new NotImplementedException("Hastebin provider not implemented yet.");
}

public sealed class AzureUploadProvider : IUploadProvider
{
    public string Name => "Azure Blob Storage";
    public string Description => "Upload to your own Azure Storage account (requires configuration).";
    public SupportedUploadType SupportedUpload => SupportedUploadType.All;
    public Task<string> UploadAsync(Stream content, string fileName, CancellationToken ct)
        => throw new NotImplementedException("Azure provider not implemented yet.");
}

public sealed class BackBlazeUploadProvider : IUploadProvider
{
    public string Name => "BackBlaze B2";
    public string Description => "Upload to BackBlaze B2 cloud storage (requires API key).";
    public SupportedUploadType SupportedUpload => SupportedUploadType.All;
    public Task<string> UploadAsync(Stream content, string fileName, CancellationToken ct)
        => throw new NotImplementedException("BackBlaze provider not implemented yet.");
}

public sealed class PicsurUploadProvider : IUploadProvider
{
    public string Name => "Picsur";
    public string Description => "Self-hostable image sharing (requires server configuration).";
    public SupportedUploadType SupportedUpload => SupportedUploadType.Image;
    public Task<string> UploadAsync(Stream content, string fileName, CancellationToken ct)
        => throw new NotImplementedException("Picsur provider not implemented yet.");
}

public sealed class VgyMeUploadProvider : IUploadProvider
{
    public string Name => "vgy.me";
    public string Description => "Free image hosting at vgy.me.";
    public SupportedUploadType SupportedUpload => SupportedUploadType.Image;
    public Task<string> UploadAsync(Stream content, string fileName, CancellationToken ct)
        => throw new NotImplementedException("vgy.me provider not implemented yet.");
}
