using System;
using System.IO;
using Amazon.Runtime;
using Amazon.S3;
using Clowd.Upload;
using Xunit;

namespace Clowd.Shared.Tests
{
    /// <summary>
    /// Verifies that the S3 provider's settings translate into the right AWS SDK client
    /// configuration — the part that actually determines third-party compatibility — and that the
    /// public URL matches the SDK's own endpoint resolution.
    /// </summary>
    public class S3UploadProviderTests
    {
        private static S3UploadProvider NewAwsProvider() => new()
        {
            AccessKeyId = "AKIAEXAMPLE",
            SecretAccessKey = "secretsecretsecret",
            BucketName = "my-bucket",
            Region = "eu-west-2",
        };

        [Fact]
        public void AwsMode_DefaultsToPathStyle_AndResolvesBuiltInRegion()
        {
            using var client = NewAwsProvider().CreateClient();
            var config = (AmazonS3Config)client.Config;

            // path-style is the safe default (DisablePathStyle == false)
            Assert.True(config.ForcePathStyle);
            Assert.Equal("eu-west-2", config.RegionEndpoint.SystemName);
            Assert.True(string.IsNullOrEmpty(config.ServiceURL));
            // checksums left at SDK defaults unless explicitly disabled
            Assert.Equal(RequestChecksumCalculation.WHEN_SUPPORTED, config.RequestChecksumCalculation);
            Assert.Equal(ResponseChecksumValidation.WHEN_SUPPORTED, config.ResponseChecksumValidation);
        }

        [Fact]
        public void DisablePathStyle_SwitchesToVirtualHostedAddressing()
        {
            var p = NewAwsProvider();
            p.DisablePathStyle = true;

            using var client = p.CreateClient();
            Assert.False(((AmazonS3Config)client.Config).ForcePathStyle);
        }

        [Fact]
        public void DisableChecksumValidation_RelaxesChecksumSettings()
        {
            var p = NewAwsProvider();
            p.DisableChecksumValidation = true;

            using var client = p.CreateClient();
            var config = (AmazonS3Config)client.Config;

            Assert.Equal(RequestChecksumCalculation.WHEN_REQUIRED, config.RequestChecksumCalculation);
            Assert.Equal(ResponseChecksumValidation.WHEN_REQUIRED, config.ResponseChecksumValidation);
        }

        [Fact]
        public void DisableChecksumValidation_AlsoDisablesStreamingPayloadSigning()
        {
            var p = NewAwsProvider();
            p.DisableChecksumValidation = true;
            using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

            var request = p.CreatePutObjectRequest(stream, "application/octet-stream", "abc/file.bin", "file.bin");

            Assert.True(request.DisableDefaultChecksumValidation);
            Assert.True(request.DisablePayloadSigning);
        }

        [Fact]
        public void CustomEndpoint_UsesServiceUrlAndCustomRegionForSigning()
        {
            var p = new S3UploadProvider
            {
                AccessKeyId = "key",
                SecretAccessKey = "secret",
                BucketName = "bucket",
                UseCustomEndpoint = true,
                CustomEndpoint = "https://s3.example-provider.com",
                Region = "auto", // e.g. Cloudflare R2's region string
            };

            using var client = p.CreateClient();
            var config = (AmazonS3Config)client.Config;

            // the SDK normalises ServiceURL with a trailing slash
            Assert.Equal("https://s3.example-provider.com", config.ServiceURL.TrimEnd('/'));
            Assert.Equal("auto", config.AuthenticationRegion);
        }

        [Fact]
        public void CreateClient_Throws_WhenCredentialsMissing()
        {
            var p = new S3UploadProvider { BucketName = "bucket", Region = "eu-west-2" };
            Assert.Throws<InvalidOperationException>(() => p.CreateClient());
        }

        [Fact]
        public void CreateClient_Throws_WhenCustomEndpointEnabledButBlank()
        {
            var p = new S3UploadProvider
            {
                AccessKeyId = "key",
                SecretAccessKey = "secret",
                BucketName = "bucket",
                UseCustomEndpoint = true,
                CustomEndpoint = "   ",
            };
            Assert.Throws<InvalidOperationException>(() => p.CreateClient());
        }

        [Fact]
        public void BuildPublicUrl_PathStyle_MatchesSdkResolution()
        {
            var p = NewAwsProvider();
            using var client = p.CreateClient();

            var url = p.BuildPublicUrl(client, "my-bucket", "abc123/file.png");
            Assert.Equal("https://s3.eu-west-2.amazonaws.com/my-bucket/abc123/file.png", url);
            Assert.DoesNotContain("?", url); // signing query stripped
        }

        [Fact]
        public void BuildPublicUrl_VirtualHosted_MatchesSdkResolution()
        {
            var p = NewAwsProvider();
            p.DisablePathStyle = true;
            using var client = p.CreateClient();

            var url = p.BuildPublicUrl(client, "my-bucket", "abc123/file.png");
            Assert.Equal("https://my-bucket.s3.eu-west-2.amazonaws.com/abc123/file.png", url);
        }

        [Fact]
        public void BuildPublicUrl_CustomDomain_OverridesAndEscapesKey()
        {
            var p = NewAwsProvider();
            p.CustomDomain = "cdn.example.com";
            using var client = p.CreateClient();

            var url = p.BuildPublicUrl(client, "my-bucket", "abc123/my file.png");
            Assert.Equal("https://cdn.example.com/abc123/my%20file.png", url);
        }
    }
}
