using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Upload
{
    /// <summary>
    /// Uploads to Backblaze B2 through its S3-compatible API by delegating to a
    /// privately-configured <see cref="S3UploadProvider"/>, so B2 shares the S3 implementation
    /// including accelerated uploads. The native B2 API is no longer used; uploads made by the
    /// retired native provider stored a B2 fileId (always "4_...") and can no longer be deleted
    /// from within Clowd.
    /// </summary>
    public class BackBlazeUploadProvider : UploadProviderBase, IAccelerateProvider
    {
        public override string Name => "BackBlaze B2";

        public override string Description => "Uploads any file to a public B2 bucket";

        public override SupportedUploadType SupportedUpload => SupportedUploadType.All;

        public override Stream Icon => new Resource().BackBlazeIcon;

        [Description("The keyID of a B2 application key. The account master key does not work with "
                    + "the S3-compatible API — create an application key instead.")]
        public string KeyId
        {
            get => _keyId;
            set => Set(ref _keyId, value);
        }

        [Description("The applicationKey secret paired with the keyID above.")]
        public string ApplicationKey
        {
            get => _applicationKey;
            set => Set(ref _applicationKey, value);
        }

        [Description("The name of the bucket to upload into. It must already exist.")]
        public string BucketName
        {
            get => _bucketName;
            set => Set(ref _bucketName, value);
        }

        [Description("The S3 endpoint shown on your B2 bucket page, e.g. s3.us-west-004.backblazeb2.com. "
                    + "The bucket's region is derived from it.")]
        public string Endpoint
        {
            get => _endpoint;
            set => Set(ref _endpoint, value);
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

        private string _keyId;
        private string _applicationKey;
        private string _bucketName;
        private string _endpoint;
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

        // records from the retired native-API provider stored the B2 fileId as UploadKey, which
        // the S3 API cannot delete by — and S3 DeleteObject on a nonexistent key reports success,
        // so refuse those rather than silently no-op.
        public override bool CanDelete(UploadDeleteInfo info)
            => info != null
               && !String.IsNullOrEmpty(info.UploadKey)
               && !info.UploadKey.StartsWith("4_", StringComparison.Ordinal)
               && !String.IsNullOrWhiteSpace(_keyId)
               && !String.IsNullOrWhiteSpace(_applicationKey)
               && !String.IsNullOrWhiteSpace(_bucketName)
               && !String.IsNullOrWhiteSpace(_endpoint);

        public override Task DeleteAsync(UploadDeleteInfo info, CancellationToken cancelToken)
            => CreateS3().DeleteAsync(info, cancelToken);

        private S3UploadProvider CreateS3()
        {
            if (String.IsNullOrWhiteSpace(_keyId) || String.IsNullOrWhiteSpace(_applicationKey))
                throw new InvalidOperationException("A BackBlaze B2 keyID and applicationKey are required.");
            if (String.IsNullOrWhiteSpace(_bucketName))
                throw new InvalidOperationException("A BackBlaze B2 bucket name is required.");

            var host = (_endpoint ?? "").Trim();
            if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                host = host.Substring("https://".Length);
            else if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                host = host.Substring("http://".Length);
            host = host.TrimEnd('/');

            var parts = host.Split('.');
            if (parts.Length != 4 || parts[0] != "s3" || parts[2] != "backblazeb2" || parts[3] != "com")
                throw new InvalidOperationException(
                    "The endpoint must be the S3 endpoint shown on your B2 bucket page, e.g. s3.us-west-004.backblazeb2.com.");

            return new S3UploadProvider
            {
                AccessKeyId = _keyId,
                SecretAccessKey = _applicationKey,
                BucketName = _bucketName,
                UseCustomEndpoint = true,
                CustomEndpoint = "https://" + host,
                Region = parts[1],
                // B2 rejects the data-integrity checksums newer AWS SDKs send by default
                DisableChecksumValidation = true,
                AccelerateUploads = _accelerateUploads,
                AccelerateServerUrl = _accelerateServerUrl,
            };
        }
    }
}
