using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Clowd.UI.Preview
{
    /// <summary>
    /// The hot cache of decoded previews, keyed by <see cref="PreviewKey"/> (session directory +
    /// content stamp). Every method is UI-thread-affine, which is the whole point: a tile's
    /// <c>Render</c> and the engine's completion drain both run there, and an
    /// <see cref="Bitmap"/> may only be constructed and drawn there anyway, so a lock would buy
    /// nothing but contention on the one thread that must never block. DEBUG builds assert the
    /// affinity rather than trusting it.
    ///
    /// <para>
    /// Recency is a true LRU: an intrusive doubly-linked list threaded through the same
    /// <see cref="Node"/> objects the dictionary holds, so a hit is one dictionary probe plus four
    /// pointer writes and no allocation. That is deliberately <i>not</i> what the converter it
    /// replaces did — <c>ImagePathToBitmapConverter</c> dropped the entire dictionary on overflow
    /// (RecentSessionsPage.axaml.cs:1140-1141), which is cheap to write and pathological to use:
    /// scrolling one row past the cap threw away every thumbnail above, so the way back up
    /// re-decoded the whole list.
    /// </para>
    ///
    /// <para>
    /// Two budgets, because either alone is wrong. Bytes bound the real cost (a tile is ~132 KB of
    /// BGRA, but the cache also holds icons a fortieth that size), and an entry count bounds the
    /// bookkeeping so a list of thousands of tiny icons cannot grow an unbounded dictionary.
    /// </para>
    /// </summary>
    public sealed class PreviewMemoryCache
    {
        /// <summary>~250 tile-sized previews at 220x150 BGRA.</summary>
        public const long MaxBytes = 32L * 1024 * 1024;

        public const int MaxEntries = 256;

        private sealed class Node
        {
            internal PreviewKey Key;
            internal Bitmap Bitmap;
            internal PreviewKind Kind;
            internal long Bytes;

            /// <summary>Toward <see cref="_newest"/>; null on the most recently used node.</summary>
            internal Node Newer;

            /// <summary>Toward <see cref="_oldest"/>; null on the eviction candidate.</summary>
            internal Node Older;
        }

        private readonly Dictionary<PreviewKey, Node> _map = new Dictionary<PreviewKey, Node>();

        private Node _newest;
        private Node _oldest;
        private long _bytes;

        /// <summary>Live entries. Diagnostics only.</summary>
        public int Count => _map.Count;

        /// <summary>Charged bytes across all live entries. Diagnostics only.</summary>
        public long Bytes => _bytes;

        /// <summary>The engine's UI-thread probe. A hit promotes the entry to most-recent, so the
        /// rows a user is actually looking at are the last ones evicted.</summary>
        public bool TryGet(in PreviewKey key, out Bitmap bmp, out PreviewKind kind)
        {
            VerifyUiThread();

            if (!_map.TryGetValue(key, out var node))
            {
                bmp = null;
                kind = PreviewKind.None;
                return false;
            }

            Touch(node);
            bmp = node.Bitmap;
            kind = node.Kind;
            return true;
        }

        /// <summary>Installs a freshly wrapped preview. <paramref name="bytes"/> is what the entry
        /// is charged against the budget; pass 0 to have it estimated from the bitmap's pixel size
        /// (4 bytes per pixel, which is what every producer here actually hands over).</summary>
        public void Set(in PreviewKey key, Bitmap bmp, PreviewKind kind, long bytes)
        {
            VerifyUiThread();

            if (bmp == null)
                return;

            if (bytes <= 0)
                bytes = EstimateBytes(bmp);

            if (_map.TryGetValue(key, out var existing))
            {
                // a re-produce for the same stamp: swap the payload in place rather than churn the
                // list, so the entry keeps its recency.
                _bytes -= existing.Bytes;
                existing.Bitmap = bmp;
                existing.Kind = kind;
                existing.Bytes = bytes;
                _bytes += bytes;
                Touch(existing);
            }
            else
            {
                var node = new Node { Key = key, Bitmap = bmp, Kind = kind, Bytes = bytes };
                _map.Add(key, node);
                _bytes += bytes;
                LinkNewest(node);
            }

            Trim();
        }

        /// <summary>Drops every entry belonging to a session directory — called when the session is
        /// deleted, so a directory reused under the same name cannot serve the old picture.
        /// O(n) over the cache, which is fine: this runs on a user-initiated delete, not on
        /// scroll.</summary>
        public void PurgeSessionDir(string dir)
        {
            VerifyUiThread();

            if (String.IsNullOrEmpty(dir) || _map.Count == 0)
                return;

            // both sides go through the same canonicalization the keys were minted with, so a
            // caller passing an unnormalized directory (SessionManager.DeleteSession hands over a
            // raw Path.GetDirectoryName) still matches.
            var target = PreviewKey.NormalizeDir(dir);
            if (String.IsNullOrEmpty(target))
                return;

            List<Node> doomed = null;
            foreach (var node in _map.Values)
            {
                if (String.Equals(node.Key.SessionDir, target, StringComparison.Ordinal))
                    (doomed ??= new List<Node>()).Add(node);
            }

            if (doomed == null)
                return;

            foreach (var node in doomed)
                Remove(node);
        }

        /// <summary>Empties the cache. Nothing in the app calls this today; it exists so a future
        /// memory-pressure hook has something to call that is not <c>new</c>.</summary>
        public void Clear()
        {
            VerifyUiThread();

            _map.Clear();
            _newest = null;
            _oldest = null;
            _bytes = 0;
        }

        /// <summary>Evicts from the old end until both budgets are met. The last entry is never
        /// evicted: a single preview larger than the whole budget should still be usable once
        /// rather than thrown away the instant it lands.</summary>
        private void Trim()
        {
            while (_map.Count > 1 && (_bytes > MaxBytes || _map.Count > MaxEntries))
            {
                var victim = _oldest;
                if (victim == null)
                    break;

                // the Bitmap is deliberately NOT disposed. A tile whose Render is mid-flight — or
                // simply one that has not been told to re-request yet — still holds this reference,
                // and Avalonia draws a disposed Bitmap as a hard failure. Dropping our reference is
                // enough; the GC reclaims it once the last tile lets go.
                Remove(victim);
            }
        }

        private void Remove(Node node)
        {
            Unlink(node);
            _map.Remove(node.Key);
            _bytes -= node.Bytes;
        }

        private void Touch(Node node)
        {
            if (ReferenceEquals(node, _newest))
                return;

            Unlink(node);
            LinkNewest(node);
        }

        private void LinkNewest(Node node)
        {
            node.Older = _newest;
            node.Newer = null;

            if (_newest != null)
                _newest.Newer = node;

            _newest = node;
            _oldest ??= node;
        }

        private void Unlink(Node node)
        {
            if (node.Newer != null)
                node.Newer.Older = node.Older;
            else if (ReferenceEquals(_newest, node))
                _newest = node.Older;

            if (node.Older != null)
                node.Older.Newer = node.Newer;
            else if (ReferenceEquals(_oldest, node))
                _oldest = node.Newer;

            node.Newer = null;
            node.Older = null;
        }

        private static long EstimateBytes(Bitmap bmp)
        {
            var size = bmp.PixelSize;
            return Math.Max(0, (long)size.Width * size.Height * 4);
        }

        [Conditional("DEBUG")]
        private static void VerifyUiThread() => Dispatcher.UIThread.VerifyAccess();
    }
}
