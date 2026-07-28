using System.Linq;
using Clowd.Config;
using Clowd.Upload;
using Xunit;

namespace Clowd.Shared.Tests
{
    /// <summary>Covers the <see cref="IBuiltInProvider"/> seeding semantics in
    /// <see cref="SettingsUpload.DiscoverProviders"/>: sorts first, on by default, claims its
    /// supported types only while they are unclaimed, and never overrides a persisted choice.</summary>
    public class BuiltInProviderTests
    {
        private static SettingsUpload Discover()
        {
            // force the Clowd.Upload assembly into the AppDomain, mirroring App startup
            _ = typeof(MimeProvider).Assembly;

            var settings = new SettingsUpload();
            settings.DiscoverProviders();
            return settings;
        }

        private static UploadProviderInfo Info<T>(SettingsUpload settings) where T : IUploadProvider
            => settings.Providers.Single(p => p.Provider is T);

        [Fact]
        public void ClowdProvider_IsBuiltInTextProvider()
        {
            var provider = new ClowdUploadProvider();

            Assert.IsAssignableFrom<IBuiltInProvider>(provider);
            Assert.Equal(SupportedUploadType.Text, provider.SupportedUpload);
            Assert.Equal("https://clwd.app", provider.ServerUrl);
            Assert.Equal("Clowd", provider.Name);
            Assert.NotNull(provider.Icon);

            // no delete endpoint on the server, so previous uploads are never deletable
            Assert.False(provider.CanDelete(new UploadDeleteInfo { UploadKey = "abcdefghij" }));
        }

        [Fact]
        public void ClowdProvider_Name_ShowsHostWhenSelfHosted()
        {
            var provider = new ClowdUploadProvider { ServerUrl = "https://paste.example.com" };
            Assert.Equal("Clowd (paste.example.com)", provider.Name);

            // a trailing slash is still the default server
            provider.ServerUrl = "https://clwd.app/";
            Assert.Equal("Clowd", provider.Name);
        }

        [Fact]
        public void DiscoverProviders_BuiltInSortsFirst_EnabledAndDefaultForText()
        {
            var settings = Discover();

            var first = settings.Providers.First();
            Assert.IsType<ClowdUploadProvider>(first.Provider);

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
            var config = settings.ProviderConfig[nameof(ClowdUploadProvider)];
            Assert.True(config.IsEnabled);
            Assert.True(config.DefaultFor.HasFlag(SupportedUploadType.Text));
            Assert.Equal("https://clwd.app", config.Settings[nameof(ClowdUploadProvider.ServerUrl)]);
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

            var clowd = Info<ClowdUploadProvider>(settings);
            var hastebin = Info<HastebinUploadProvider>(settings);

            // still enabled (its own key is missing) but the user's default stands
            Assert.True(clowd.IsEnabled);
            Assert.False(clowd.DefaultFor.HasFlag(SupportedUploadType.Text));
            Assert.Same(hastebin, settings.GetDefaultProvider(SupportedUploadType.Text));
        }

        [Fact]
        public void DiscoverProviders_PersistedBuiltInConfigWins()
        {
            _ = typeof(MimeProvider).Assembly;

            var settings = new SettingsUpload();
            settings.ProviderConfig[nameof(ClowdUploadProvider)] = new UploadProviderConfig
            {
                IsEnabled = false,
                DefaultFor = SupportedUploadType.None,
                Settings = { [nameof(ClowdUploadProvider.ServerUrl)] = "https://paste.example.com" },
            };

            settings.DiscoverProviders();

            var clowd = Info<ClowdUploadProvider>(settings);
            Assert.False(clowd.IsEnabled);
            Assert.False(clowd.DefaultFor.HasFlag(SupportedUploadType.Text));
            Assert.Null(settings.GetDefaultProvider(SupportedUploadType.Text));
            Assert.Equal("https://paste.example.com", ((ClowdUploadProvider)clowd.Provider).ServerUrl);

            // sorting is by type, not by state — a disabled built-in still leads the list
            Assert.Same(clowd, settings.Providers.First());
        }
    }
}
