using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.Config;
using Clowd.Upload;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class UploadSettingsTransferTests
    {
        private static SettingsUpload NewSettings()
        {
            // force the Clowd.Upload assembly into the AppDomain, mirroring App startup
            _ = typeof(MimeProvider).Assembly;

            var settings = new SettingsUpload();
            settings.DiscoverProviders();
            return settings;
        }

        private static UploadTransferPayload Export(SettingsUpload settings, params string[] typeNames)
            => settings.ExportProviders(typeNames, "4.0.0", DateTimeOffset.UnixEpoch);

        [Fact]
        public void RoundTrip_CarriesSettingsDefaultsAndEnabledState()
        {
            var source = NewSettings();

            var imgur = source.Providers.Single(p => p.Provider is ImgurUploadProvider);
            imgur.IsEnabled = true;
            ((ImgurUploadProvider)imgur.Provider).ClientId = "test-client-id";
            source.SetDefaultProvider(imgur, SupportedUploadType.Image);

            var s3Info = source.Providers.Single(p => p.Provider is S3UploadProvider);
            s3Info.IsEnabled = true;
            var s3 = (S3UploadProvider)s3Info.Provider;
            s3.AccessKeyId = "AKIAEXAMPLE";
            s3.SecretAccessKey = "secretsecret";
            s3.BucketName = "my-bucket";
            s3.UseCustomEndpoint = true;
            source.SetDefaultProvider(s3Info, SupportedUploadType.Binary);

            var text = UploadSettingsTransfer.Encode(Export(source, "ImgurUploadProvider", "S3UploadProvider"));

            Assert.True(UploadSettingsTransfer.TryDecode(text, out var payload));
            Assert.Equal(2, payload.Providers.Count);

            var target = NewSettings();
            foreach (var kvp in payload.Providers)
                Assert.True(target.ImportProvider(kvp.Key, kvp.Value));

            var importedImgur = target.Providers.Single(p => p.Provider is ImgurUploadProvider);
            Assert.True(importedImgur.IsEnabled);
            Assert.Equal("test-client-id", ((ImgurUploadProvider)importedImgur.Provider).ClientId);
            Assert.Same(importedImgur, target.GetDefaultProvider(SupportedUploadType.Image));

            var importedS3Info = target.Providers.Single(p => p.Provider is S3UploadProvider);
            var importedS3 = (S3UploadProvider)importedS3Info.Provider;
            Assert.True(importedS3Info.IsEnabled);
            Assert.Equal("AKIAEXAMPLE", importedS3.AccessKeyId);
            Assert.Equal("secretsecret", importedS3.SecretAccessKey);
            Assert.Equal("my-bucket", importedS3.BucketName);
            Assert.True(importedS3.UseCustomEndpoint);
            Assert.Same(importedS3Info, target.GetDefaultProvider(SupportedUploadType.Binary));

            // and the imported state is mirrored into the persisted config
            Assert.True(target.ProviderConfig["ImgurUploadProvider"].IsEnabled);
            Assert.Equal("test-client-id", target.ProviderConfig["ImgurUploadProvider"].Settings["ClientId"]);
        }

        [Fact]
        public void ExportedString_IsNotLegible()
        {
            var source = NewSettings();
            var imgur = source.Providers.Single(p => p.Provider is ImgurUploadProvider);
            ((ImgurUploadProvider)imgur.Provider).ClientId = "super-secret-client-id";

            var text = UploadSettingsTransfer.Encode(Export(source, "ImgurUploadProvider"));

            Assert.DoesNotContain("super-secret-client-id", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Imgur", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Encode_ProducesADifferentStringEachTime()
        {
            // a fresh nonce per export, so two exports of identical settings are not obviously
            // the same string
            var source = NewSettings();
            var payload = Export(source, "ImgurUploadProvider");

            Assert.NotEqual(UploadSettingsTransfer.Encode(payload), UploadSettingsTransfer.Encode(payload));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("hello world")]
        [InlineData("aGVsbG8gd29ybGQ=")] // valid base64, wrong magic
        public void TryDecode_RejectsForeignText(string text)
        {
            Assert.False(UploadSettingsTransfer.TryDecode(text, out var payload));
            Assert.Null(payload);
        }

        [Fact]
        public void TryDecode_ToleratesWhitespaceFromWrappedText()
        {
            var source = NewSettings();
            var text = UploadSettingsTransfer.Encode(Export(source, "ImgurUploadProvider"));

            var wrapped = String.Join(Environment.NewLine, Chunk(text, 40));
            Assert.True(UploadSettingsTransfer.TryDecode("  " + wrapped + "  ", out var payload));
            Assert.True(payload.Providers.ContainsKey("ImgurUploadProvider"));
        }

        [Fact]
        public void TryDecode_RejectsTamperedPayload()
        {
            var source = NewSettings();
            var text = UploadSettingsTransfer.Encode(Export(source, "ImgurUploadProvider"));

            var raw = Convert.FromBase64String(text);
            raw[^1] ^= 0xff;

            Assert.False(UploadSettingsTransfer.TryDecode(Convert.ToBase64String(raw), out _));
        }

        [Fact]
        public void TryDecode_RejectsUnsupportedVersion()
        {
            var source = NewSettings();
            var raw = Convert.FromBase64String(UploadSettingsTransfer.Encode(Export(source, "ImgurUploadProvider")));

            raw[6] = 200; // envelope version byte

            Assert.False(UploadSettingsTransfer.TryDecode(Convert.ToBase64String(raw), out _));
        }

        [Fact]
        public void Import_TakesTheDefaultSlotFromWhicheverProviderHeldIt()
        {
            var source = NewSettings();
            var imgur = source.Providers.Single(p => p.Provider is ImgurUploadProvider);
            imgur.IsEnabled = true;
            source.SetDefaultProvider(imgur, SupportedUploadType.Image);

            var target = NewSettings();
            var vgy = target.Providers.Single(p => p.Provider is VgyMeUploadProvider);
            vgy.IsEnabled = true;
            target.SetDefaultProvider(vgy, SupportedUploadType.Image);

            UploadSettingsTransfer.TryDecode(
                UploadSettingsTransfer.Encode(Export(source, "ImgurUploadProvider")), out var payload);
            target.ImportProvider("ImgurUploadProvider", payload.Providers["ImgurUploadProvider"]);

            Assert.False(vgy.DefaultFor.HasFlag(SupportedUploadType.Image));
            Assert.Same(
                target.Providers.Single(p => p.Provider is ImgurUploadProvider),
                target.GetDefaultProvider(SupportedUploadType.Image));
        }

        [Fact]
        public void Import_IgnoresProvidersThisBuildDoesNotHave()
        {
            var target = NewSettings();

            var entry = new UploadTransferEntry { Name = "Ghost", IsEnabled = true };
            Assert.False(target.ImportProvider("GhostUploadProvider", entry));
            Assert.False(target.ProviderConfig.ContainsKey("GhostUploadProvider"));
        }

        [Fact]
        public void Import_IgnoresSettingsKeysThisBuildDoesNotHave()
        {
            var target = NewSettings();

            var entry = new UploadTransferEntry
            {
                Name = "Imgur",
                IsEnabled = true,
                DefaultFor = "Image",
                Settings = new Dictionary<string, string>
                {
                    ["ClientId"] = "known",
                    ["SomethingFromTheFuture"] = "unknown",
                },
            };

            Assert.True(target.ImportProvider("ImgurUploadProvider", entry));

            var imgur = target.Providers.Single(p => p.Provider is ImgurUploadProvider);
            Assert.True(imgur.IsEnabled);
            Assert.Equal("known", ((ImgurUploadProvider)imgur.Provider).ClientId);
        }

        [Fact]
        public void ParseUploadTypes_KeepsTheNamesItRecognises()
        {
            var parsed = UploadSettingsTransfer.ParseUploadTypes("Image, SomethingNewer, Text");

            Assert.True(parsed.HasFlag(SupportedUploadType.Image));
            Assert.True(parsed.HasFlag(SupportedUploadType.Text));
            Assert.False(parsed.HasFlag(SupportedUploadType.Video));
        }

        [Fact]
        public void ParseUploadTypes_RoundTripsWhatFormatWrites()
        {
            var types = SupportedUploadType.Image | SupportedUploadType.Video;
            var text = UploadSettingsTransfer.FormatUploadTypes(types);

            var parsed = UploadSettingsTransfer.ParseUploadTypes(text);
            Assert.True(parsed.HasFlag(SupportedUploadType.Image));
            Assert.True(parsed.HasFlag(SupportedUploadType.Video));
            Assert.False(parsed.HasFlag(SupportedUploadType.Binary));
        }

        [Fact]
        public void ExportProviders_SkipsNamesThatMatchNothing()
        {
            var source = NewSettings();
            var payload = Export(source, "ImgurUploadProvider", "GhostUploadProvider");

            Assert.Equal(new[] { "ImgurUploadProvider" }, payload.Providers.Keys);
        }

        [Fact]
        public void ExportProviders_ReadsProvidersTheUserNeverTouched()
        {
            // a provider with no ProviderConfig entry yet must still export its live state
            var source = NewSettings();
            Assert.False(source.ProviderConfig.ContainsKey("VgyMeUploadProvider"));

            var payload = Export(source, "VgyMeUploadProvider");
            Assert.True(payload.Providers.ContainsKey("VgyMeUploadProvider"));
            Assert.Equal("vgy.me", payload.Providers["VgyMeUploadProvider"].Name);
        }

        private static IEnumerable<string> Chunk(string value, int size)
        {
            for (int i = 0; i < value.Length; i += size)
                yield return value.Substring(i, Math.Min(size, value.Length - i));
        }
    }
}
