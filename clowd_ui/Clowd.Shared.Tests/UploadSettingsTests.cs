using System;
using System.IO;
using System.Linq;
using Clowd.Config;
using Clowd.Upload;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class UploadSettingsTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ClowdSettingsTests", Guid.NewGuid() + ".json");

        public void Dispose()
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch { }
        }

        [Fact]
        public void DiscoverProviders_FindsShippedProviders()
        {
            // force the Clowd.Upload assembly into the AppDomain, mirroring App startup
            _ = typeof(MimeProvider).Assembly;

            var settings = new SettingsUpload();
            settings.DiscoverProviders();

            var names = settings.Providers.Select(p => p.Provider.GetType().Name).ToArray();
            Assert.Contains("ImgurUploadProvider", names);
            Assert.Contains("AzureUploadProvider", names);
            Assert.Contains("BackBlazeUploadProvider", names);
            Assert.Contains("CatboxUploadProvider", names);
            Assert.Contains("HastebinUploadProvider", names);
            Assert.Contains("PicsurUploadProvider", names);
            Assert.Contains("VgyMeUploadProvider", names);

            // all providers start disabled with no defaults
            Assert.All(settings.Providers, p => Assert.False(p.IsEnabled));
        }

        [Fact]
        public void ProviderChanges_MirrorIntoConfig_AndRoundTrip()
        {
            _ = typeof(MimeProvider).Assembly;

            var original = new SettingsRoot();
            original.Uploads.DiscoverProviders();

            var imgur = original.Uploads.Providers.Single(p => p.Provider is ImgurUploadProvider);
            imgur.IsEnabled = true;
            ((ImgurUploadProvider)imgur.Provider).ClientId = "test-client-id";
            original.Uploads.SetDefaultProvider(imgur, SupportedUploadType.Image);

            var catbox = original.Uploads.Providers.Single(p => p.Provider is CatboxUploadProvider);
            catbox.IsEnabled = true;
            ((CatboxUploadProvider)catbox.Provider).ExpireUploads = CatboxUploadProvider.CatBoxExpiry._24h;

            SettingsService.Save(original, _path);
            var loaded = SettingsService.Load(_path);
            loaded.Uploads.DiscoverProviders();

            var loadedImgur = loaded.Uploads.Providers.Single(p => p.Provider is ImgurUploadProvider);
            Assert.True(loadedImgur.IsEnabled);
            Assert.Equal("test-client-id", ((ImgurUploadProvider)loadedImgur.Provider).ClientId);
            Assert.True(loadedImgur.DefaultFor.HasFlag(SupportedUploadType.Image));
            Assert.Same(loadedImgur, loaded.Uploads.GetDefaultProvider(SupportedUploadType.Image));

            var loadedCatbox = loaded.Uploads.Providers.Single(p => p.Provider is CatboxUploadProvider);
            Assert.True(loadedCatbox.IsEnabled);
            Assert.Equal(CatboxUploadProvider.CatBoxExpiry._24h, ((CatboxUploadProvider)loadedCatbox.Provider).ExpireUploads);

            // providers that were never touched stay disabled
            var loadedAzure = loaded.Uploads.Providers.Single(p => p.Provider is AzureUploadProvider);
            Assert.False(loadedAzure.IsEnabled);
        }

        [Fact]
        public void Save_SucceedsWhileFileHeldOpenWithoutDeleteSharing()
        {
            // regression: an editor (Zed) holding the settings file open denies the atomic
            // replace-move with UnauthorizedAccessException; Save must fall back to an
            // in-place write rather than throwing and stranding the .~tmp file.
            var original = new SettingsRoot();
            original.General.LastSavePath = @"C:\before";
            SettingsService.Save(original, _path);

            using (var holder = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                original.General.LastSavePath = @"C:\after";
                SettingsService.Save(original, _path);
            }

            var loaded = SettingsService.Load(_path);
            Assert.Equal(@"C:\after", loaded.General.LastSavePath);

            // no temp files left behind
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_path), Path.GetFileName(_path) + "*.~tmp"));
        }

        [Fact]
        public void SetDefaultProvider_IsExclusivePerType()
        {
            _ = typeof(MimeProvider).Assembly;

            var settings = new SettingsUpload();
            settings.DiscoverProviders();

            var imgur = settings.Providers.Single(p => p.Provider is ImgurUploadProvider);
            var vgy = settings.Providers.Single(p => p.Provider is VgyMeUploadProvider);
            imgur.IsEnabled = true;
            vgy.IsEnabled = true;

            settings.SetDefaultProvider(imgur, SupportedUploadType.Image);
            settings.SetDefaultProvider(vgy, SupportedUploadType.Image);

            Assert.False(imgur.DefaultFor.HasFlag(SupportedUploadType.Image));
            Assert.True(vgy.DefaultFor.HasFlag(SupportedUploadType.Image));
            Assert.Same(vgy, settings.GetDefaultProvider(SupportedUploadType.Image));

            settings.ClearDefaultProvider(vgy, SupportedUploadType.Image);
            Assert.Null(settings.GetDefaultProvider(SupportedUploadType.Image));
        }
    }
}
