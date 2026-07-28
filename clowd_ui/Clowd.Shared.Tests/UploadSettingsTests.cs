using System;
using System.ComponentModel;
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
            Assert.Contains("S3UploadProvider", names);
            Assert.Contains("ClowdUploadProvider", names);

            // third-party providers start disabled with no defaults (built-ins are seeded on —
            // see BuiltInProviderTests)
            Assert.All(
                settings.Providers.Where(p => p.Provider is not IBuiltInProvider),
                p => Assert.False(p.IsEnabled));
        }

        [Fact]
        public void S3Provider_SettingsRoundTrip_IncludingBoolCompatibilityToggles()
        {
            _ = typeof(MimeProvider).Assembly;

            var original = new SettingsRoot();
            original.Uploads.DiscoverProviders();

            var s3Info = original.Uploads.Providers.Single(p => p.Provider is S3UploadProvider);
            s3Info.IsEnabled = true;
            var s3 = (S3UploadProvider)s3Info.Provider;
            s3.AccessKeyId = "AKIAEXAMPLE";
            s3.SecretAccessKey = "secretsecret";
            s3.BucketName = "my-bucket";
            s3.Region = "eu-west-2";
            s3.UseCustomEndpoint = true;
            s3.CustomEndpoint = "https://s3.example.com";
            s3.DisablePathStyle = true;
            s3.DisableChecksumValidation = true;
            s3.MakeObjectsPublic = true;
            s3.CustomDomain = "cdn.example.com";

            SettingsService.Save(original, _path);
            var loaded = SettingsService.Load(_path);
            loaded.Uploads.DiscoverProviders();

            var loadedInfo = loaded.Uploads.Providers.Single(p => p.Provider is S3UploadProvider);
            var l = (S3UploadProvider)loadedInfo.Provider;

            Assert.True(loadedInfo.IsEnabled);
            Assert.Equal("AKIAEXAMPLE", l.AccessKeyId);
            Assert.Equal("secretsecret", l.SecretAccessKey);
            Assert.Equal("my-bucket", l.BucketName);
            Assert.Equal("eu-west-2", l.Region);
            Assert.True(l.UseCustomEndpoint);
            Assert.Equal("https://s3.example.com", l.CustomEndpoint);
            Assert.True(l.DisablePathStyle);
            Assert.True(l.DisableChecksumValidation);
            Assert.True(l.MakeObjectsPublic);
            Assert.Equal("cdn.example.com", l.CustomDomain);
        }

        [Fact]
        public void S3Provider_Region_SuggestsBuiltInAwsRegions()
        {
            // the enumerated list feeds the editable region dropdown
            var regions = S3UploadProvider.GetKnownRegions().ToList();
            Assert.Contains("eu-west-2", regions);
            Assert.Contains("us-east-1", regions);

            // and the Region property carries the attribute that resolves that same list
            var pd = TypeDescriptor.GetProperties(typeof(S3UploadProvider))[nameof(S3UploadProvider.Region)];
            var attr = pd.Attributes.OfType<SuggestedValuesAttribute>().Single();
            Assert.Contains("eu-west-2", attr.GetValues());
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
