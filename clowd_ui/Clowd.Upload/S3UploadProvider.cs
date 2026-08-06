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
using Clowd.Upload.Accelerate;

namespace Clowd.Upload
{
    /// <summary>
    /// Uploads to Amazon S3 or any S3-compatible service (MinIO, Cloudflare R2, Backblaze B2,
    /// Wasabi, …). The <see cref="DisablePathStyle"/> / <see cref="DisableChecksumValidation"/>
    /// toggles and the custom-endpoint fields exist to accommodate third-party providers that
    /// diverge from Amazon's request format.
    /// </summary>
    public class S3UploadProvider : UploadProviderBase, IAccelerateProvider
    {
        public override string Name => "Amazon S3";

        public override string Description => "Uploads any file to an Amazon S3 or S3-compatible bucket";

        public override SupportedUploadType SupportedUpload => SupportedUploadType.All;

        public override Stream Icon => new Resource().S3Icon;

        public override bool SupportsUnseekableUpload => true;

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

        [Description("Use UNSIGNED-PAYLOAD and don't send or require the extra data-integrity checksums added by newer AWS SDKs. "
                    + "Turn on if your provider rejects uploads with a checksum or streaming-signature error.")]
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
        private bool _accelerateUploads = true;
        private string _accelerateServerUrl = "https://clwd.app";

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
            if (!fileStream.CanSeek)
                return await UploadUnseekableAsync(fileStream, progress, uploadName, cancelToken);

            var mimeType = _mimeDb.GetMimeFromExtension(Path.GetExtension(uploadName)).ContentType;
            var key = GetObjectKey(uploadName);

            using var client = CreateClient();

            var request = CreatePutObjectRequest(fileStream, mimeType, key, uploadName);

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

        // the accelerate protocol needs the ContentLength up front, so non-seekable streams skip
        // acceleration and go direct via the multipart path instead.
        public override Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, UploadUrlHandler urlAvailable,
            string uploadName, CancellationToken cancelToken)
            => AccelerateUploads && fileStream.CanSeek
                ? UploadAcceleratedAsync(fileStream, progress, urlAvailable, uploadName, cancelToken)
                : UploadAsync(fileStream, progress, uploadName, cancelToken);

        /// <summary>Streams a payload of unknown length through the low-level multipart API:
        /// 16 MiB is buffered at a time (comfortably above the 5 MiB part minimum) and uploaded
        /// as one part, so nothing is ever spooled to disk.</summary>
        private async Task<UploadResult> UploadUnseekableAsync(Stream fileStream, UploadProgressHandler progress, string uploadName,
            CancellationToken cancelToken)
        {
            const int partBufferSize = 16 * 1024 * 1024;

            var mimeType = _mimeDb.GetMimeFromExtension(Path.GetExtension(uploadName)).ContentType;
            var bucket = _bucketName.Trim();
            var key = GetObjectKey(uploadName);

            using var client = CreateClient();

            var init = new InitiateMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                ContentType = mimeType,
                CannedACL = _makeObjectsPublic ? S3CannedACL.PublicRead : null,
            };
            init.Headers.ContentDisposition = $"inline; filename=\"{uploadName}\"";

            var initiated = await client.InitiateMultipartUploadAsync(init, cancelToken);

            try
            {
                var partETags = new List<PartETag>();
                var buffer = new byte[partBufferSize];
                long uploaded = 0;

                for (int partNumber = 1;; partNumber++)
                {
                    var filled = await FillBufferAsync(fileStream, buffer, cancelToken);

                    // an empty first part is still sent — completing a multipart upload with
                    // zero parts is rejected.
                    if (filled == 0 && partNumber > 1)
                        break;

                    var part = new UploadPartRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        UploadId = initiated.UploadId,
                        PartNumber = partNumber,
                        PartSize = filled,
                        InputStream = new MemoryStream(buffer, 0, filled, false),
                        IsLastPart = filled < buffer.Length,
                        // same compatibility toggles as CreatePutObjectRequest
                        DisablePayloadSigning = _disableChecksumValidation ? true : (bool?)null,
                        DisableDefaultChecksumValidation = _disableChecksumValidation ? true : (bool?)null,
                    };

                    if (progress != null)
                    {
                        var before = uploaded;
                        part.StreamTransferProgress += (_, e) => progress(before + e.TransferredBytes);
                    }

                    var resp = await client.UploadPartAsync(part, cancelToken);
                    partETags.Add(new PartETag { PartNumber = partNumber, ETag = resp.ETag });
                    uploaded += filled;

                    if (filled < buffer.Length)
                        break;
                }

                await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = initiated.UploadId,
                    PartETags = partETags,
                }, cancelToken);
            }
            catch
            {
                // abort (not cancellable — cleanup should run even when the upload was cancelled)
                // so the parts don't linger as billable orphans.
                try
                {
                    await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        UploadId = initiated.UploadId,
                    }, CancellationToken.None);
                }
                catch
                { }

                throw;
            }

            return new UploadResult
            {
                Provider = this,
                PublicUrl = BuildPublicUrl(client, bucket, key),
                FileName = uploadName,
                ContentType = mimeType,
                UploadKey = key,
                UploadTime = DateTimeOffset.UtcNow,
            };
        }

        private static async Task<int> FillBufferAsync(Stream stream, byte[] buffer, CancellationToken cancelToken)
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancelToken);
                if (read == 0)
                    break;
                filled += read;
            }

            return filled;
        }

        internal PutObjectRequest CreatePutObjectRequest(Stream fileStream, string mimeType, string key, string uploadName)
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucketName.Trim(),
                Key = key,
                InputStream = fileStream,
                ContentType = mimeType,
                // the base wraps the file stream in a `using`; don't let the SDK close it out from under that.
                AutoCloseStream = false,
                // R2 and some other S3-compatible services do not implement the SDK's streaming SigV4 payload format.
                DisablePayloadSigning = _disableChecksumValidation ? true : (bool?)null,
                // belt-and-suspenders with the config-level checksum settings applied in CreateClient().
                DisableDefaultChecksumValidation = _disableChecksumValidation ? true : (bool?)null,
                // grant public read at the object level (only honoured by buckets with ACLs enabled).
                CannedACL = _makeObjectsPublic ? S3CannedACL.PublicRead : null,
            };

            // filename hint + inline rendering, matching the Azure provider's behaviour.
            request.Headers.ContentDisposition = $"inline; filename=\"{uploadName}\"";
            return request;
        }

        private async Task<UploadResult> UploadAcceleratedAsync(Stream fileStream, UploadProgressHandler progress,
            UploadUrlHandler urlAvailable, string uploadName, CancellationToken cancelToken)
        {
            var mimeType = _mimeDb.GetMimeFromExtension(Path.GetExtension(uploadName)).ContentType;
            var bucket = _bucketName.Trim();
            var key = GetObjectKey(uploadName);
            var contentLength = fileStream.Length;

            // the client owns the chunk plan; partUrls below are minted one-per-chunk for it. 16 MiB
            // is inside the server's [5,32] MiB clamp so the plan survives the round-trip unchanged.
            var chunkSize = AcceleratedUploadClient.ClampChunkSize(AcceleratedUploadClient.DefaultChunkSize);
            var chunkCount = AcceleratedUploadClient.ComputeChunkCount(contentLength, chunkSize);

            using var client = CreateClient();

            var init = new InitiateMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                ContentType = mimeType,
                CannedACL = _makeObjectsPublic ? S3CannedACL.PublicRead : null,
            };
            init.Headers.ContentDisposition = $"inline; filename=\"{uploadName}\"";

            var initiated = await client.InitiateMultipartUploadAsync(init, cancelToken);
            var uploadId = initiated.UploadId;

            var descriptor = BuildS3Descriptor(client, bucket, key, uploadId, chunkCount);

            return await AcceleratedUploadRunner.RunAsync(
                AccelerateServerUrl, descriptor, fileStream, mimeType, contentLength, uploadName, key,
                chunkSize, this, progress, urlAvailable, cancelToken);
        }

        /// <summary>Builds the s3-multipart destination descriptor: a presigned UploadPart URL for
        /// every chunk plus presigned Complete/Abort URLs, all SigV4 query-signed with
        /// UNSIGNED-PAYLOAD. All URLs are derived from the SDK's own endpoint resolution (via a
        /// stripped presigned GET) so path-style / virtual-hosted / custom-endpoint / region choices
        /// match exactly what the SDK would use.</summary>
        private DestinationDescriptor BuildS3Descriptor(AmazonS3Client client, string bucket, string key, string uploadId, int chunkCount)
        {
            // the real S3 object URL (host + path), independent of any CustomDomain override.
            var signedGet = client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddMinutes(5),
            });
            var baseUri = new Uri(signedGet.Split('?')[0]);

            // match the SDK's signing scope: the configured region, defaulting to us-east-1 when a
            // custom endpoint is used without one (the AWS SDK's own default signing region).
            var region = String.IsNullOrWhiteSpace(_region) ? "us-east-1" : _region.Trim();
            var accessKey = _accessKeyId.Trim();
            var secretKey = _secretAccessKey.Trim();
            var expires = TimeSpan.FromHours(48);
            var now = DateTimeOffset.UtcNow;

            var partUrls = new string[chunkCount];
            for (int n = 0; n < chunkCount; n++)
            {
                partUrls[n] = SigV4Presigner.Presign("PUT", baseUri,
                    new Dictionary<string, string> { ["partNumber"] = (n + 1).ToString(), ["uploadId"] = uploadId },
                    accessKey, secretKey, region, "s3", expires, now);
            }

            var completeUrl = SigV4Presigner.Presign("POST", baseUri,
                new Dictionary<string, string> { ["uploadId"] = uploadId },
                accessKey, secretKey, region, "s3", expires, now);

            var abortUrl = SigV4Presigner.Presign("DELETE", baseUri,
                new Dictionary<string, string> { ["uploadId"] = uploadId },
                accessKey, secretKey, region, "s3", expires, now);

            return new DestinationDescriptor
            {
                Type = "s3-multipart",
                PartUrls = partUrls,
                CompleteUrl = completeUrl,
                AbortUrl = abortUrl,
                FinalUrl = BuildPublicUrl(client, bucket, key),
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
            using (var client = CreateClient())
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = _bucketName.Trim(),
                    Key = info.UploadKey,
                }, cancelToken);
            }

            // accelerated records also carry a clwd.app short link — remove it too.
            if (AcceleratedDeleteToken.TryParse(info.DeleteKey, out var id, out var token))
                await AcceleratedUploadClient.DeleteAsync(AccelerateServerUrl, id, token, cancelToken);
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
