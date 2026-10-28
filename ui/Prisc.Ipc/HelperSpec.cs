using System;

namespace Prisc.Ipc
{
    /// <summary>An <c>error</c> event of any catalog (HELPER_PROTOCOL.md §2.2). <see cref="Code"/> is one of
    /// <see cref="IpcErrorCodes"/>; a code the parent does not know is treated as fatal.</summary>
    public interface IIpcError
    {
        string Code { get; }
        string Message { get; }
        bool Fatal { get; }
    }

    /// <summary>One catalog as the C# side uses it: what <c>hello</c> must match, the quit grace, and the
    /// generated codec. The instances are the generated <see cref="HelperSpecs"/>.</summary>
    public sealed class HelperSpec<TEvent, TCommand> where TEvent : class where TCommand : class
    {
        public HelperSpec(string catalog, string catalogHash, TimeSpan quitGrace,
            Func<string, TEvent> decode, Func<TCommand, string> encode,
            Func<TEvent, string> encodeEvent, Func<string, TCommand> decodeCommand,
            Func<TEvent, bool> isTerminal)
        {
            Catalog = catalog;
            CatalogHash = catalogHash;
            QuitGrace = quitGrace;
            Decode = decode;
            Encode = encode;
            EncodeEvent = encodeEvent;
            DecodeCommand = decodeCommand;
            IsTerminal = isTerminal;
        }

        /// <summary>The catalog name, as sent in <c>hello.catalog</c>.</summary>
        public string Catalog { get; }

        /// <summary>The generated fingerprint <c>hello.catalog_hash</c> must equal.</summary>
        public string CatalogHash { get; }

        /// <summary>The wait between <c>quit</c> and kill (HELPER_PROTOCOL.md §7.3).</summary>
        public TimeSpan QuitGrace { get; }

        /// <summary>Parent side: one stdout line to an event; null for an unknown <c>type</c>.</summary>
        public Func<string, TEvent> Decode { get; }

        /// <summary>Parent side: a command to one stdin line (no newline).</summary>
        public Func<TCommand, string> Encode { get; }

        /// <summary>Child side: an event to one stdout line (no newline).</summary>
        public Func<TEvent, string> EncodeEvent { get; }

        /// <summary>Child side: one stdin line to a command; null for an unknown <c>type</c>.</summary>
        public Func<string, TCommand> DecodeCommand { get; }

        /// <summary>A terminal event or a fatal error (HELPER_PROTOCOL.md §1.4).</summary>
        public Func<TEvent, bool> IsTerminal { get; }
    }

    /// <summary>A golden sample line from the catalogs, with the generated codec's decode-then-encode.</summary>
    internal sealed record HelperSample(string Catalog, string Name, string Line, Func<string, string> RoundTrip);
}
