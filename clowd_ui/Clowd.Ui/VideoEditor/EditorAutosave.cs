using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Clowd.VideoSDK.Editing;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// Where an <see cref="EditorSession"/>'s project lands on disk: a latest-wins background
    /// writer over one file. The session serializes on the UI thread (cheap — a few KB of JSON) and
    /// hands the bytes here; the write itself is queued behind the previous one on the thread pool,
    /// so a drag that produces a save per pointer move can never stall the UI on a disk write and
    /// can never leave two writes racing for the file. Only the newest bytes are ever written: a
    /// queued write that finds a newer set waiting simply writes those, and the runs after it find
    /// nothing to do.
    ///
    /// Debouncing is <b>not</b> here — that is the session's injected save scheduler (a dispatcher
    /// timer in the window), which is what decides how often bytes arrive.
    ///
    /// A failed write is logged and swallowed: the edit file is convenience, and losing it must
    /// never take the editing session down with it.
    /// </summary>
    internal sealed class EditorAutosave : IEditorPersistence
    {
        /// <summary>How long <see cref="Flush"/> waits for an in-flight write before writing the
        /// pending bytes itself. Bounded so a hung disk cannot hold the window open.</summary>
        private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

        private readonly string _path;
        private readonly object _queueLock = new object();
        private byte[] _pending;
        private Task _writeTask = Task.CompletedTask;

        /// <param name="path">Full path to the edit file; the directory must already exist (the
        /// session directory does).</param>
        public EditorAutosave(string path)
        {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentException("The autosave path is empty.", nameof(path));

            _path = path;
        }

        public void Write(byte[] utf8Json)
        {
            if (utf8Json == null)
                return;

            Interlocked.Exchange(ref _pending, utf8Json);
            lock (_queueLock)
                _writeTask = _writeTask.ContinueWith(_ => WritePending(), TaskScheduler.Default);
        }

        /// <summary>Writes whatever is still pending, synchronously — the window's close path, so a
        /// save that was scheduled a moment ago cannot be lost.</summary>
        public void Flush()
        {
            Task inFlight;
            lock (_queueLock)
                inFlight = _writeTask;

            try { inFlight.Wait(FlushTimeout); }
            catch {; }

            WritePending();
        }

        private void WritePending()
        {
            var bytes = Interlocked.Exchange(ref _pending, null);
            if (bytes == null)
                return;

            try
            {
                File.WriteAllBytes(_path, bytes);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to persist videoedit.json: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "videoeditor.persist-doc");
            }
        }
    }
}
