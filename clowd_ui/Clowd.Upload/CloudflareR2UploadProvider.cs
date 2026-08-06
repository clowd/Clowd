using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Upload
{
    /// <summary>
    /// Uploads to Cloudflare R2 through its S3-compatible API by delegating to a
    /// privately-configured <see cref="S3UploadProvider"/>, so R2 shares the S3 implementation
    /// including accelerated uploads. R2's S3 endpoint is never publicly readable, so shareable
    /// links require the bucket's r2.dev URL or a connected custom domain.
    /// </summary>
    public class CloudflareR2UploadProvider : UploadProviderBase, IAccelerateProvider
    {
        public override string Name => "Cloudflare R2";

        public override string Description => "Uploads any file to a Cloudflare R2 bucket";

        public override SupportedUploadType SupportedUpload => SupportedUploadType.All;

        public override Stream Icon => new Resource().CloudflareIcon;

        // uploads delegate to S3UploadProvider, which handles non-seekable streams via multipart
        public override bool SupportsUnseekableUpload => true;

        [Description("Your Cloudflare account ID (the 32-character hex string shown in the R2 dashboard). "
                    + "You can also paste the bucket's full S3 endpoint, e.g. <account>.r2.cloudflarestorage.com.")]
        public string AccountId
        {
            get => _accountId;
            set => Set(ref _accountId, value);
        }

        [Description("The Access Key ID of an R2 API token with read & write access to the bucket.")]
        public string AccessKeyId
        {
            get => _accessKeyId;
            set => Set(ref _accessKeyId, value);
        }

        [Description("The Secret Access Key paired with the Access Key ID above.")]
        public string SecretAccessKey
        {
            get => _secretAccessKey;
            set => Set(ref _secretAccessKey, value);
        }

        [Description("The name of the bucket to upload into. It must already exist.")]
        public string BucketName
        {
            get => _bucketName;
            set => Set(ref _bucketName, value);
        }

        [Description("The bucket's public base URL — its r2.dev development URL (https://pub-….r2.dev) or a "
                    + "custom domain connected to the bucket. R2's S3 endpoint is not publicly readable, so "
                    + "shared links will not work without this.")]
        public string PublicUrl
        {
            get => _publicUrl;
            set => Set(ref _publicUrl, value);
        }

        [Description("Route uploads through clwd.app so a shareable link is ready immediately, "
                    + "while the file relays to this bucket in the background.")]
        public bool AccelerateUploads
        {
            get => _accelerateUploads;
            set => Set(ref _accelerateUploads, value);
        }

        [Description("The accelerate server to route uploads through when the toggle above is on.")]
        public string AccelerateServerUrl
        {
            get => _accelerateServerUrl;
            set => Set(ref _accelerateServerUrl, value);
        }

        private string _accountId;
        private string _accessKeyId;
        private string _secretAccessKey;
        private string _bucketName;
        private string _publicUrl;
        private bool _accelerateUploads = true;
        private string _accelerateServerUrl = "https://clwd.app";

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName,
            CancellationToken cancelToken)
        {
            var result = await CreateS3().UploadAsync(fileStream, progress, uploadName, cancelToken);
            result.Provider = this;
            return result;
        }

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, UploadUrlHandler urlAvailable,
            string uploadName, CancellationToken cancelToken)
        {
            var result = await CreateS3().UploadAsync(fileStream, progress, urlAvailable, uploadName, cancelToken);
            result.Provider = this;
            return result;
        }

        public override bool CanDelete(UploadDeleteInfo info)
            => info != null
               && !String.IsNullOrEmpty(info.UploadKey)
               && !String.IsNullOrWhiteSpace(_accessKeyId)
               && !String.IsNullOrWhiteSpace(_secretAccessKey)
               && !String.IsNullOrWhiteSpace(_bucketName)
               && !String.IsNullOrWhiteSpace(_accountId);

        public override Task DeleteAsync(UploadDeleteInfo info, CancellationToken cancelToken)
            => CreateS3().DeleteAsync(info, cancelToken);

        private S3UploadProvider CreateS3()
        {
            if (String.IsNullOrWhiteSpace(_accessKeyId) || String.IsNullOrWhiteSpace(_secretAccessKey))
                throw new InvalidOperationException("A Cloudflare R2 Access Key ID and Secret Access Key are required.");
            if (String.IsNullOrWhiteSpace(_bucketName))
                throw new InvalidOperationException("A Cloudflare R2 bucket name is required.");

            var account = (_accountId ?? "").Trim();
            if (account.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                account = account.Substring("https://".Length);
            else if (account.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                account = account.Substring("http://".Length);
            account = account.TrimEnd('/');

            // a bare account ID becomes the standard endpoint; a pasted endpoint (including
            // jurisdiction forms like <account>.eu.r2.cloudflarestorage.com) is used as-is.
            var host = account.Contains('.') ? account : account + ".r2.cloudflarestorage.com";
            if (account.Length == 0 || !host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The account ID must be your Cloudflare account ID or the bucket's S3 endpoint, "
                    + "e.g. <account>.r2.cloudflarestorage.com.");

            return new S3UploadProvider
            {
                AccessKeyId = _accessKeyId,
                SecretAccessKey = _secretAccessKey,
                BucketName = _bucketName,
                UseCustomEndpoint = true,
                CustomEndpoint = "https://" + host,
                Region = "auto",
                // R2 does not implement the SDK's streaming SigV4 payload format or optional checksums.
                DisableChecksumValidation = true,
                CustomDomain = _publicUrl,
                AccelerateUploads = _accelerateUploads,
                AccelerateServerUrl = _accelerateServerUrl,
            };
        }
    }
}
