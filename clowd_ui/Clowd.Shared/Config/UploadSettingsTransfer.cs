using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clowd.Config
{
    /// <summary>One provider's exported state. Deliberately loose: every field is optional and
    /// unknown fields are ignored on read, so a string written by a newer (or older) Clowd still
    /// imports whatever it has in common with this build.</summary>
    public class UploadTransferEntry
    {
        /// <summary>The provider's display name at export time, so the import picker can label an
        /// entry even when this build has no provider of that type.</summary>
        public string Name { get; set; }

        public bool IsEnabled { get; set; }

        /// <summary>Comma-separated <see cref="SupportedUploadType"/> names rather than the enum
        /// itself — an unrecognized name is dropped instead of failing the whole import.</summary>
        public string DefaultFor { get; set; }

        /// <summary>The provider's flattened settings, same shape as
        /// <see cref="UploadProviderConfig.Settings"/>.</summary>
        public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>The decrypted body of a transfer string.</summary>
    public class UploadTransferPayload
    {
        /// <summary>Payload schema, separate from the envelope version byte. Bumped only when the
        /// JSON shape changes in a way readers need to know about.</summary>
        public int Schema { get; set; } = 1;

        /// <summary>Clowd version that produced the string. Informational only.</summary>
        public string App { get; set; }

        public DateTimeOffset Exported { get; set; }

        /// <summary>Keyed by provider type name (e.g. "ImgurUploadProvider"), matching
        /// <see cref="SettingsUpload.ProviderConfig"/>.</summary>
        public Dictionary<string, UploadTransferEntry> Providers { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Packs upload provider settings into a single clipboard-friendly string and back.
    /// </summary>
    /// <remarks>
    /// <para>Wire format is JSON, encrypted with AES-256-GCM, wrapped in a small binary envelope,
    /// then Base64:</para>
    /// <code>
    /// [0..6)   "CLWDUP"  magic
    /// [6]      version   envelope version, 0-255
    /// [7..19)  nonce     12 bytes
    /// [19..35) tag       16 bytes (authenticates the header too, so the version cannot be edited)
    /// [35..)   ciphertext
    /// </code>
    /// <para>The key is compiled into Clowd, so this is <b>obfuscation, not secrecy</b>: anyone
    /// with a copy of Clowd (or a disassembler) can read an exported string. Its job is only to
    /// stop credentials being legible to someone who stumbles across the text with no context —
    /// pasted into the wrong chat window, left in a clipboard manager, and so on. Treat an
    /// exported string as being as sensitive as the credentials inside it. A baked-in RSA keypair
    /// would be no stronger for the same reason (both halves have to ship for import to work), and
    /// RSA cannot encrypt a payload this size without a symmetric key anyway.</para>
    /// </remarks>
    public static class UploadSettingsTransfer
    {
        /// <summary>Envelope version written by this build.</summary>
        public const byte CurrentVersion = 1;

        /// <summary>Highest envelope version this build knows how to read.</summary>
        public const byte MaxSupportedVersion = 1;

        private static readonly byte[] _magic = { (byte)'C', (byte)'L', (byte)'W', (byte)'D', (byte)'U', (byte)'P' };

        private const int HeaderLength = 7; // magic + version byte
        private const int NonceLength = 12;
        private const int TagLength = 16;

        // Obfuscation key — see the remarks above before treating this as a secret.
        private static readonly byte[] _key =
        {
            0x9d, 0x41, 0xf6, 0x2a, 0x0b, 0x77, 0xc5, 0x18,
            0xe3, 0x8a, 0x54, 0x6f, 0x21, 0xbc, 0x90, 0x3d,
            0x7e, 0x15, 0xa8, 0xcb, 0x62, 0x04, 0xdf, 0x93,
            0x38, 0xe7, 0x1a, 0x76, 0xc0, 0x4b, 0x85, 0x2f,
        };

        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Serializes, encrypts and Base64-encodes <paramref name="payload"/>.</summary>
        public static string Encode(UploadTransferPayload payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, _json);

            var output = new byte[HeaderLength + NonceLength + TagLength + plaintext.Length];
            Buffer.BlockCopy(_magic, 0, output, 0, _magic.Length);
            output[_magic.Length] = CurrentVersion;

            var header = output.AsSpan(0, HeaderLength);
            var nonce = output.AsSpan(HeaderLength, NonceLength);
            var tag = output.AsSpan(HeaderLength + NonceLength, TagLength);
            var cipher = output.AsSpan(HeaderLength + NonceLength + TagLength);

            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(_key, TagLength);
            aes.Encrypt(nonce, plaintext, cipher, tag, header);

            return Convert.ToBase64String(output);
        }

        /// <summary>
        /// Reverses <see cref="Encode"/>. Returns false — without throwing — for anything that is
        /// not one of ours: arbitrary clipboard text, a truncated copy, a newer envelope version,
        /// or a payload that fails authentication.
        /// </summary>
        public static bool TryDecode(string text, out UploadTransferPayload payload)
        {
            payload = null;

            if (String.IsNullOrWhiteSpace(text))
                return false;

            byte[] raw;
            try
            {
                // FromBase64String already skips whitespace, so a string that wrapped across lines
                // on its way through a chat client still decodes.
                raw = Convert.FromBase64String(text.Trim());
            }
            catch (FormatException)
            {
                return false;
            }

            if (raw.Length < HeaderLength + NonceLength + TagLength)
                return false;

            for (int i = 0; i < _magic.Length; i++)
            {
                if (raw[i] != _magic[i])
                    return false;
            }

            var version = raw[_magic.Length];
            if (version == 0 || version > MaxSupportedVersion)
                return false;

            var header = raw.AsSpan(0, HeaderLength);
            var nonce = raw.AsSpan(HeaderLength, NonceLength);
            var tag = raw.AsSpan(HeaderLength + NonceLength, TagLength);
            var cipher = raw.AsSpan(HeaderLength + NonceLength + TagLength);

            var plaintext = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(_key, TagLength);
                aes.Decrypt(nonce, cipher, tag, plaintext, header);
            }
            catch (CryptographicException)
            {
                return false;
            }

            try
            {
                payload = JsonSerializer.Deserialize<UploadTransferPayload>(plaintext, _json);
            }
            catch (JsonException)
            {
                return false;
            }

            if (payload == null)
                return false;

            payload.Providers ??= new Dictionary<string, UploadTransferEntry>(StringComparer.Ordinal);
            return true;
        }

        /// <summary>Formats a <see cref="SupportedUploadType"/> for
        /// <see cref="UploadTransferEntry.DefaultFor"/>.</summary>
        public static string FormatUploadTypes(SupportedUploadType types) => types.ToString();

        /// <summary>
        /// Parses <see cref="UploadTransferEntry.DefaultFor"/> a name at a time, ignoring any the
        /// running build does not know. A whole-string <c>Enum.Parse</c> would reject
        /// "Image, SomethingNewer" outright and lose the Image default with it.
        /// </summary>
        public static SupportedUploadType ParseUploadTypes(string value)
        {
            var result = SupportedUploadType.None;

            if (String.IsNullOrWhiteSpace(value))
                return result;

            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<SupportedUploadType>(part, ignoreCase: true, out var parsed))
                    result |= parsed;
            }

            return result;
        }
    }
}
