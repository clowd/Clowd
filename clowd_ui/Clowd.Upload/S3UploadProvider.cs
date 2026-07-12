using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Clowd.Config;

namespace Clowd.Upload
{
    /// <summary>
    /// Uploads to Amazon S3 or any S3-compatible service (MinIO, Cloudflare R2, Backblaze B2,
    /// Wasabi, …). The <see cref="DisablePathStyle"/> / <see cref="DisableChecksumValidation"/>
    /// toggles and the custom-endpoint fields exist to accommodate third-party providers that
    /// diverge from Amazon's request format.
    /// </summary>
    public class S3UploadProvider : UploadProviderBase
    {
        public override string Name => "Amazon S3";

        public override string Description => "Uploads any file to an Amazon S3 or S3-compatible bucket";

        public override SupportedUploadType SupportedUpload => SupportedUploadType.All;

        public override Stream Icon => new Resource().S3Icon;

        [Description("Your AWS access key ID (or the equivalent key for your S3-compatible provider).")]
        public string AccessKeyId
        {
            get => _accessKeyId;
            set => Set(ref _accessKeyId, value);
        }

        [Description("The secret access key paired with the access key ID above.")]
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

        [SuggestedValues(typeof(S3UploadProvider), nameof(GetKnownRegions))]
        [Description("The bucket's region, e.g. eu-west-2. With a custom endpoint, enter the region string "
                    + "your provider expects (e.g. 'auto' for Cloudflare R2, 'us-east-1' for many others).")]
        public string Region
        {
            get => _region;
            set => Set(ref _region, value);
        }

        [Description("Send to a non-AWS, S3-compatible service instead of Amazon S3. Enables the endpoint URL below.")]
        public bool UseCustomEndpoint
        {
            get => _useCustomEndpoint;
            set => Set(ref _useCustomEndpoint, value);
        }

        [Description("The S3 endpoint URL of your provider, e.g. https://<account>.r2.cloudflarestorage.com. "
                    + "Only used when 'Use custom endpoint' is enabled.")]
        public string CustomEndpoint
        {
            get => _customEndpoint;
            set => Set(ref _customEndpoint, value);
        }

        [Description("Use virtual-hosted-style URLs (bucket.endpoint/key) instead of path-style (endpoint/bucket/key). "
                    + "Leave off for most third-party providers, which expect path-style addressing.")]
        public bool DisablePathStyle
        {
            get => _disablePathStyle;
            set => Set(ref _disablePathStyle, value);
        }

        [Description("Don't send or require the extra data-integrity checksums added by newer AWS SDKs. "
                    + "Turn on if your provider rejects uploads with a checksum or signature error.")]
        public bool DisableChecksumValidation
        {
            get => _disableChecksumValidation;
            set => Set(ref _disableChecksumValidation, value);
        }

        [Description("Set a public-read ACL on each uploaded object so its link works without signing in. "
                    + "Requires a bucket with ACLs enabled (Object Ownership: 'bucket owner preferred'). "
                    + "On buckets with ACLs disabled (the AWS default), leave this off and grant public "
                    + "read with a bucket policy instead — an ACL here will be rejected.")]
        public bool MakeObjectsPublic
        {
            get => _makeObjectsPublic;
            set => Set(ref _makeObjectsPublic, value);
        }

        [Description("Optional. Return links under this domain (a CDN or custom domain mapped to the bucket root) "
                    + "instead of the raw S3 URL.")]
        public string CustomDomain
        {
            get => _customDomain;
            set => Set(ref _customDomain, value);
        }

        private string _accessKeyId;
        private string _secretAccessKey;
        private string _bucketName;
        private string _region;
        private bool _useCustomEndpoint;
        private string _customEndpoint;
        private bool _disablePathStyle;
        private bool _disableChecksumValidation;
        private bool _makeObjectsPublic;
        private string _customDomain;

        private readonly IMimeProvider _mimeDb = new MimeProvider();

        /// <summary>The built-in AWS region system names (e.g. "eu-west-2"), enumerated from the SDK.
        /// Referenced by <see cref="SuggestedValuesAttribute"/> on <see cref="Region"/>.</summary>
        public static IEnumerable<string> GetKnownRegions()
            => RegionEndpoint.EnumerableAllRegions
                             .Select(r => r.SystemName)
                             .Where(s => !String.IsNullOrEmpty(s))
                             .Distinct()
                             .OrderBy(s => s, StringComparer.Ordinal);

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName,
            CancellationToken cancelToken)
        {
            var mimeType = _mimeDb.GetMimeFromExtension(Path.GetExtension(uploadName)).ContentType;
            var key = GetObjectKey(uploadName);

            using var client = CreateClient();

            var request = new PutObjectRequest
            {
                BucketName = _bucketName.Trim(),
                Key = key,
                InputStream = fileStream,
                ContentType = mimeType,
                // the base wraps the file stream in a `using`; don't let the SDK close it out from under that.
                AutoCloseStream = false,
                // belt-and-suspenders with the config-level checksum settings applied in CreateClient().
                DisableDefaultChecksumValidation = _disableChecksumValidation ? true : (bool?)null,
                // grant public read at the object level (only honoured by buckets with ACLs enabled).
                CannedACL = _makeObjectsPublic ? S3CannedACL.PublicRead : null,
            };

            // filename hint + inline rendering, matching the Azure provider's behaviour.
            request.Headers.ContentDisposition = $"inline; filename=\"{uploadName}\"";

            if (progress != null)
                request.StreamTransferProgress += (_, e) => progress(e.TransferredBytes);

            await client.PutObjectAsync(request, cancelToken);

            return new UploadResult
            {
                Provider = this,
                PublicUrl = BuildPublicUrl(client, _bucketName.Trim(), key),
                FileName = uploadName,
                ContentType = mimeType,
                UploadKey = key,
                UploadTime = DateTimeOffset.UtcNow,
            };
        }

        public override bool CanDelete(UploadDeleteInfo info)
            => info != null
               && !String.IsNullOrEmpty(info.UploadKey)
               && !String.IsNullOrWhiteSpace(_accessKeyId)
               && !String.IsNullOrWhiteSpace(_secretAccessKey)
               && !String.IsNullOrWhiteSpace(_bucketName);

        public override async Task DeleteAsync(UploadDeleteInfo info, CancellationToken cancelToken)
        {
            using var client = CreateClient();
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucketName.Trim(),
                Key = info.UploadKey,
            }, cancelToken);
        }

        /// <summary>Builds an <see cref="AmazonS3Client"/> from the current settings, validating the
        /// combination and translating the compatibility toggles into SDK config. Shared by upload
        /// and delete so both honour the same endpoint/region/path-style/checksum choices.</summary>
        internal AmazonS3Client CreateClient()
        {
            if (String.IsNullOrWhiteSpace(_accessKeyId) || String.IsNullOrWhiteSpace(_secretAccessKey))
                throw new InvalidOperationException("The S3 access key and secret key must be configured.");

            if (String.IsNullOrWhiteSpace(_bucketName))
                throw new InvalidOperationException("An S3 bucket name must be configured.");

            var config = new AmazonS3Config
            {
                // default addressing is path-style (endpoint/bucket/key), the safest for third-party
                // providers; DisablePathStyle switches to virtual-hosted addressing.
                ForcePathStyle = !_disablePathStyle,
            };

            if (_useCustomEndpoint)
            {
                if (String.IsNullOrWhiteSpace(_customEndpoint))
                    throw new InvalidOperationException("A custom endpoint URL must be provided when 'Use custom endpoint' is enabled.");

                config.ServiceURL = _customEndpoint.Trim();

                // a custom endpoint has no built-in region; the user-supplied string is used only for
                // request signing (SigV4 credential scope).
                if (!String.IsNullOrWhiteSpace(_region))
                    config.AuthenticationRegion = _region.Trim();
            }
            else
            {
                if (String.IsNullOrWhiteSpace(_region))
                    throw new InvalidOperationException("An AWS region must be selected.");

                config.RegionEndpoint = RegionEndpoint.GetBySystemName(_region.Trim());
            }

            if (_disableChecksumValidation)
            {
                config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
                config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
            }

            return new AmazonS3Client(new BasicAWSCredentials(_accessKeyId.Trim(), _secretAccessKey.Trim()), config);
        }

        /// <summary>The public URL of an uploaded object. Uses a stripped pre-signed URL so the
        /// host and path-style choice always match the SDK's own endpoint resolution; a configured
        /// <see cref="CustomDomain"/> overrides it.</summary>
        internal string BuildPublicUrl(AmazonS3Client client, string bucket, string key)
        {
            if (!String.IsNullOrWhiteSpace(_customDomain))
            {
                var domain = _customDomain.Trim().TrimEnd('/');
                if (!domain.Contains("://"))
                    domain = "https://" + domain;

                var escapedKey = String.Join("/", key.Split('/').Select(Uri.EscapeDataString));
                return $"{domain}/{escapedKey}";
            }

            var signed = client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddMinutes(5),
            });

            return signed.Split('?')[0];
        }

        private static string GetObjectKey(string uploadName)
        {
            // a short random prefix avoids collisions while keeping the original filename in the URL.
            var prefix = Guid.NewGuid().ToString("N").Substring(0, 12);
            var name = String.IsNullOrWhiteSpace(uploadName) ? "file" : uploadName;
            return $"{prefix}/{name}";
        }
    }
}
