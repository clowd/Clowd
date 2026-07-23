using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Clowd.Upload.Accelerate
{
    /// <summary>
    /// A minimal AWS Signature Version 4 <em>query</em> presigner (AWS4-HMAC-SHA256) that signs a
    /// single request with <c>X-Amz-SignedHeaders=host</c> and a fixed <c>UNSIGNED-PAYLOAD</c>
    /// body hash. The AWS SDK cannot presign the S3 <c>CompleteMultipartUpload</c> POST (its
    /// <see cref="Amazon.S3.HttpVerb"/> has no POST member) and cannot guarantee UNSIGNED-PAYLOAD
    /// for every operation, so the accelerated upload path hand-rolls all of its S3 capability URLs
    /// (UploadPart / Complete / Abort) through this one code path. UNSIGNED-PAYLOAD is required
    /// because the clwd.app relay PUTs bodies and XML the client cannot hash ahead of time.
    /// </summary>
    internal static class SigV4Presigner
    {
        private const string Algorithm = "AWS4-HMAC-SHA256";
        private const string UnsignedPayload = "UNSIGNED-PAYLOAD";

        /// <summary>
        /// Presigns <paramref name="baseUri"/> (scheme + host + path only — no query) for
        /// <paramref name="method"/>, folding <paramref name="extraQuery"/> (e.g. uploadId,
        /// partNumber) into the signed canonical query string. The host, path and any port on
        /// <paramref name="baseUri"/> are signed and returned verbatim, so the caller controls
        /// path-style vs virtual-hosted addressing and the endpoint host by how it builds the URI.
        /// </summary>
        public static string Presign(
            string method,
            Uri baseUri,
            IReadOnlyDictionary<string, string> extraQuery,
            string accessKey,
            string secretKey,
            string region,
            string service,
            TimeSpan expiresIn,
            DateTimeOffset signTimeUtc)
        {
            if (baseUri == null)
                throw new ArgumentNullException(nameof(baseUri));

            var host = baseUri.IsDefaultPort ? baseUri.Host : baseUri.Host + ":" + baseUri.Port.ToString(CultureInfo.InvariantCulture);

            // the canonical URI must be the exact (single-encoded) path we also emit on the wire.
            var canonicalUri = baseUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            canonicalUri = "/" + canonicalUri.TrimStart('/');

            var amzDate = signTimeUtc.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var dateStamp = signTimeUtc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";

            var query = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (extraQuery != null)
                foreach (var kv in extraQuery)
                    query[kv.Key] = kv.Value;

            query["X-Amz-Algorithm"] = Algorithm;
            query["X-Amz-Credential"] = accessKey + "/" + credentialScope;
            query["X-Amz-Date"] = amzDate;
            query["X-Amz-Expires"] = ((long)expiresIn.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            query["X-Amz-SignedHeaders"] = "host";

            var canonicalQuery = string.Join("&", query.Select(kv => UriEncode(kv.Key) + "=" + UriEncode(kv.Value)));

            var canonicalHeaders = "host:" + host + "\n";
            const string signedHeaders = "host";

            var canonicalRequest = string.Join("\n",
                method,
                canonicalUri,
                canonicalQuery,
                canonicalHeaders,
                signedHeaders,
                UnsignedPayload);

            var stringToSign = string.Join("\n",
                Algorithm,
                amzDate,
                credentialScope,
                Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest))));

            var signingKey = DeriveSigningKey(secretKey, dateStamp, region, service);
            var signature = Hex(HmacSha256(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

            return $"{baseUri.Scheme}://{host}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}";
        }

        private static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region, string service)
        {
            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), Encoding.UTF8.GetBytes(dateStamp));
            var kRegion = HmacSha256(kDate, Encoding.UTF8.GetBytes(region));
            var kService = HmacSha256(kRegion, Encoding.UTF8.GetBytes(service));
            return HmacSha256(kService, Encoding.UTF8.GetBytes("aws4_request"));
        }

        // RFC 3986 encoding as required by SigV4 (unreserved = A-Za-z0-9-._~; everything else
        // percent-encoded with upper-case hex). Uri.EscapeDataString already follows RFC 3986.
        private static string UriEncode(string value)
            => Uri.EscapeDataString(value ?? string.Empty);

        private static byte[] Sha256(byte[] data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data);
        }

        private static byte[] HmacSha256(byte[] key, byte[] data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(data);
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
