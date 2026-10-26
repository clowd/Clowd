using System;

namespace Clowd.Upload.Accelerate
{
    /// <summary>
    /// Encodes the (upload id, delete token) pair an accelerated upload needs to later remove its
    /// clwd.app short link into the single <see cref="UploadResult.DeleteKey"/> string that is
    /// persisted per upload record. Legacy (non-accelerated) records store a plain provider delete
    /// key (or none) that does not carry the prefix, so <see cref="TryParse"/> returns false for
    /// them and the delete path skips the server call — keeping legacy deletes byte-for-byte
    /// unchanged.
    /// </summary>
    internal static class AcceleratedDeleteToken
    {
        private const string Prefix = "clwd:v1:";

        public static string Encode(string id, string deleteToken)
            => $"{Prefix}{id}:{deleteToken}";

        public static bool TryParse(string value, out string id, out string deleteToken)
        {
            id = null;
            deleteToken = null;

            if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            // "clwd:v1:{id}:{token}" — the token may itself contain ':', so bound the split.
            var parts = value.Split(':', 4);
            if (parts.Length != 4 || parts[2].Length == 0 || parts[3].Length == 0)
                return false;

            id = parts[2];
            deleteToken = parts[3];
            return true;
        }
    }
}
