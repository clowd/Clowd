using System.Linq;
using Prisc.Config;
using Prisc.Upload;
using Xunit;

namespace Prisc.Shared.Tests
{
    /// <summary>Covers the <see cref="IBuiltInProvider"/> seeding semantics in
    /// <see cref="SettingsUpload.DiscoverProviders"/>: sorts first, on by default, claims its
    /// supported types only while they are unclaimed, and never overrides a persisted choice.</summary>
    public class BuiltInProviderTests
    {
        private static SettingsUpload Discover()
        {
            // force the Prisc.Upload assembly into the AppDomain, mirroring App startup
            _ = typeof(MimeProvider).Assembly;

            var settings = new SettingsUpload();
            settings.DiscoverProviders();
            return settings;
        }

        private static UploadProviderInfo Info<T>(SettingsUpload settings) where T : IUploadProvider
            => settings.Providers.Single(p => p.Provider is T);

        [Fact]
        public void PriscProvider_IsBuiltInTextProvider()
        {
            var provider = new PriscUploadProvider();

            Assert.IsAssignableFrom<IBuiltInProvider>(provider);
            Assert.Equal(SupportedUploadType.Text, provider.SupportedUpload);
            Assert.Equal("https://clwd.app", provider.ServerUrl);
            Assert.Equal("Prisc", provider.Name);
            Assert.NotNull(provider.Icon);

            // no delete endpoint on the server, so previous uploads are never deletable
            Assert.False(provider.CanDelete(new UploadDeleteInfo { UploadKey = "abcdefghij" }));
        }

        [Fact]
        public void PriscProvider_Name_ShowsHostWhenSelfHosted()
        {
            var provider = new PriscUploadProvider { ServerUrl = "https://paste.example.com" };
            Assert.Equal("Prisc (paste.example.com)", provider.Name);

            // a trailing slash is still the default server
            provider.ServerUrl = "https://clwd.app/";
            Assert.Equal("Prisc", provider.Name);
        }

        [Fact]
        public void DiscoverProviders_BuiltInSortsFirst_EnabledAndDefaultForText()
        {
            var settings = Discover();

            var first = settings.Providers.First();
            Assert.IsType<PriscUploadProvider>(first.Provider);

            // ...even though other providers sort before it ordinally
            Assert.Contains(settings.Providers.Skip(1), p => p.Provider is S3UploadProvider);

            Assert.True(first.IsEnabled);
            Assert.True(first.DefaultFor.HasFlag(SupportedUploadType.Text));
            Assert.Same(first, settings.GetDefaultProvider(SupportedUploadType.Text));

            // seeding only claims the types the provider actually supports
            Assert.False(first.DefaultFor.HasFlag(SupportedUploadType.Image));
            Assert.Null(settings.GetDefaultProvider(SupportedUploadType.Image));

            // and the other shipped providers are untouched
            Assert.All(settings.Providers.Where(p => p.Provider is not IBuiltInProvider), p => Assert.False(p.IsEnabled));
        }

        [Fact]
        public void DiscoverProviders_SeedingIsMirroredIntoConfig()
        {
            var settings = Discover();

            // the claim happens after the subscription is attached, so it persists — making the
            // seed one-shot rather than something that re-applies on every startup
            var config = settings.ProviderConfig[nameof(PriscUploadProvider)];
            Assert.True(config.IsEnabled);
            Assert.True(config.DefaultFor.HasFlag(SupportedUploadType.Text));
            Assert.Equal("https://clwd.app", config.Settings[nameof(PriscUploadProvider.ServerUrl)]);
        }

        [Fact]
        public void DiscoverProviders_DoesNotStompAnExistingTextDefault()
        {
            _ = typeof(MimeProvider).Assembly;

            var settings = new SettingsUpload();
            settings.ProviderConfig[nameof(HastebinUploadProvider)] = new UploadProviderConfig
            {
                IsEnabled = true,
                DefaultFor = SupportedUploadType.Text,
            };

            settings.DiscoverProviders();

            var prisc = Info<PriscUploadProvider>(settings);
            var hastebin = Info<HastebinUploadProvider>(settings);

            // still enabled (its own key is missing) but the user's default stands
            Assert.True(prisc.IsEnabled);
            Assert.False(prisc.DefaultFor.HasFlag(SupportedUploadType.Text));
            Assert.Same(hastebin, settings.GetDefaultProvider(SupportedUploadType.Text));
        }

        [Fact]
        public void DiscoverProviders_PersistedBuiltInConfigWins()
        {
            _ = typeof(MimeProvider).Assembly;

            var settings = new SettingsUpload();
            settings.ProviderConfig[nameof(PriscUploadProvider)] = new UploadProviderConfig
            {
                IsEnabled = false,
                DefaultFor = SupportedUploadType.None,
                Settings = { [nameof(PriscUploadProvider.ServerUrl)] = "https://paste.example.com" },
            };

            settings.DiscoverProviders();

            var prisc = Info<PriscUploadProvider>(settings);
            Assert.False(prisc.IsEnabled);
            Assert.False(prisc.DefaultFor.HasFlag(SupportedUploadType.Text));
            Assert.Null(settings.GetDefaultProvider(SupportedUploadType.Text));
            Assert.Equal("https://paste.example.com", ((PriscUploadProvider)prisc.Provider).ServerUrl);

            // sorting is by type, not by state — a disabled built-in still leads the list
            Assert.Same(prisc, settings.Providers.First());
        }
    }
}
