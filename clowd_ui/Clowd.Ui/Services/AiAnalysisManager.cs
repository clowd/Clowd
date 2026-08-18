using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.VideoSDK.Ai;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.Services
{
    /// <summary>The two sidecar files an edit can require per referenced stream — the denoised
    /// audio wav and the person-matte mp4 (see <see cref="AiSidecars"/>).</summary>
    public enum AiSidecarKind
    {
        Denoise,
        Matte,
    }

    /// <summary>Identity of one sidecar an edit requires: which generator, over which stream of
    /// which source.</summary>
    public readonly record struct AiSidecarKey(AiSidecarKind Kind, Guid SourceId, int StreamIndex);

    public enum AiAnalysisState
    {
        /// <summary>Waiting for the (single) generation worker to reach it.</summary>
        Queued,

        Running,

        /// <summary>Generated this session; the preview refresh has been (or is being) raised.</summary>
        Completed,

        /// <summary>Generation threw. Sticky until the requiring setting is toggled off and on —
        /// requeueing on every project change would grind the same failure forever.</summary>
        Failed,

        /// <summary>No <c>clowd_tractnni</c> binary resolves (or there is nowhere to cache — the
        /// dev harness has no session directory). Re-probed on every structural change, so a
        /// binary appearing mid-session upgrades this to a queued job.</summary>
        Unavailable,
    }

    /// <summary>An immutable snapshot of one job's state, for the inspector's status rows.</summary>
    public sealed class AiAnalysisStatus
    {
        internal AiAnalysisStatus(AiSidecarKey key, AiAnalysisState state, double progress, string detail)
        {
            Key = key;
            State = state;
            Progress = progress;
            Detail = detail;
        }

        public AiSidecarKey Key { get; }

        public AiAnalysisState State { get; }

        /// <summary>0..1, meaningful while <see cref="State"/> is Running.</summary>
        public double Progress { get; }

        /// <summary>The failure's exception message (which for a child-process failure carries the
        /// named exit code and the stderr tail), non-null only when <see cref="State"/> is
        /// Failed — the inspector's tooltip and the failure dialog both read it.</summary>
        public string Detail { get; }
    }

    /// <summary>Raised once per failed generation run, carrying what the status row cannot fit.</summary>
    public sealed class AiJobFailedEventArgs : EventArgs
    {
        internal AiJobFailedEventArgs(AiSidecarKey key, string detail)
        {
            Key = key;
            Detail = detail;
        }

        public AiSidecarKey Key { get; }

        public string Detail { get; }
    }

    /// <summary>
    /// Keeps the AI sidecar files beside <c>videoedit.json</c> in step with what the edit asks
    /// for: every structural project change recomputes which sidecars the model requires
    /// (<see cref="RequiredSidecars"/> — a track with <see cref="Track.Denoise"/> needs the
    /// denoise wav of each stream it plays, an item whose <see cref="Item.Effect"/> needs a matte
    /// needs the matte mp4 of its stream), validates them against <see cref="AiSidecars"/>, and
    /// queues what is missing or stale onto one background worker (the generators saturate the
    /// inference process on their own — running two at once would just fight over it).
    ///
    /// <para>Owned by the editor window, one per session, and disposed with it. A completed job
    /// raises <see cref="SidecarCompleted"/> on the UI thread — the window pushes a fresh snapshot
    /// through the player's own update path so the preview picks the new file up — and every
    /// state/progress move raises <see cref="Changed"/> for the inspector's status rows
    /// (<see cref="GetStatus"/>). Turning the requiring setting off (or closing the window)
    /// cancels the job it queued; a failure is logged and shown, never thrown — the composition
    /// degrades to the raw stream exactly as a missing sidecar does.</para>
    /// </summary>
    public sealed class AiAnalysisManager : IDisposable
    {
        private readonly EditorSession _session;
        private readonly string _cacheDir; // null in the dev harness (nowhere to cache)

        private readonly object _sync = new object();
        private readonly Dictionary<AiSidecarKey, Entry> _entries = new Dictionary<AiSidecarKey, Entry>();
        private readonly Queue<AiSidecarKey> _queue = new Queue<AiSidecarKey>();
        private bool _workerRunning;
        private bool _disposed;

        private sealed class Entry
        {
            public AiAnalysisState State;
            public double Progress;
            public CancellationTokenSource Cts;
            public Source Source;
            public string FailureDetail;
        }

        /// <summary>Some job's state or progress moved. Raised on the UI thread.</summary>
        public event EventHandler Changed;

        /// <summary>A sidecar finished generating and is valid on disk. Raised on the UI thread —
        /// the window hands the player a fresh snapshot so playback adopts it.</summary>
        public event EventHandler SidecarCompleted;

        /// <summary>A generation run threw. Raised on the UI thread, once per run (the Failed state
        /// is sticky) — the window turns it into a dialog, since the status row only has room for
        /// "Analysis failed".</summary>
        public event EventHandler<AiJobFailedEventArgs> JobFailed;

        public AiAnalysisManager(EditorSession session, string cacheDir)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
            _cacheDir = cacheDir;

            _session.ProjectChanged += Session_ProjectChanged;
            Refresh();
        }

        /// <summary>
        /// The sidecars this project requires, deduplicated, in model order. Pure — file validity
        /// (and therefore what actually needs generating) is the caller's second step, so the
        /// decision logic stays testable without a disk.
        /// </summary>
        public static IReadOnlyList<AiSidecarKey> RequiredSidecars(Project project)
        {
            var keys = new List<AiSidecarKey>();
            if (project?.Items == null || project.Tracks == null)
                return keys;

            var denoiseTracks = new HashSet<Guid>(project.Tracks
                .Where(t => t.Kind == TrackKind.Audio && t.Denoise)
                .Select(t => t.Id));

            var seen = new HashSet<AiSidecarKey>();
            foreach (var item in project.Items)
            {
                if (item.Content is not MediaContent media)
                    continue;

                if (denoiseTracks.Contains(item.TrackId))
                {
                    var key = new AiSidecarKey(AiSidecarKind.Denoise, media.SourceId, media.StreamIndex);
                    if (seen.Add(key))
                        keys.Add(key);
                }

                if (item.Effect != null && VideoEffect.NeedsMatte(item.Effect.Kind))
                {
                    var key = new AiSidecarKey(AiSidecarKind.Matte, media.SourceId, media.StreamIndex);
                    if (seen.Add(key))
                        keys.Add(key);
                }
            }

            return keys;
        }

        /// <summary>The job's current state, or null when there is none — the sidecar is already
        /// valid on disk, or nothing requires it.</summary>
        public AiAnalysisStatus GetStatus(AiSidecarKind kind, Guid sourceId, int streamIndex)
        {
            var key = new AiSidecarKey(kind, sourceId, streamIndex);
            lock (_sync)
            {
                return _entries.TryGetValue(key, out var entry)
                    ? new AiAnalysisStatus(key, entry.State, entry.Progress, entry.FailureDetail)
                    : null;
            }
        }

        private void Session_ProjectChanged(object sender, ProjectChangedEventArgs e)
        {
            // requirements only move on structural edits (the denoise toggle, an effect gaining
            // or losing its matte need, undo across either); mapping changes and mid-gesture
            // previews must not cost a file probe per spinner tick.
            if (e.Kind == ProjectChangeKind.Structural)
                Refresh();
        }

        /// <summary>Reconciles the job table with what the project requires right now. Runs on the
        /// session's mutation thread (the UI thread).</summary>
        private void Refresh()
        {
            bool changed = false;
            lock (_sync)
            {
                if (_disposed)
                    return;

                var project = _session.Project;
                var required = RequiredSidecars(project);
                var requiredSet = new HashSet<AiSidecarKey>(required);

                // the requiring setting was turned off: cancel the job and drop its row.
                foreach (var key in _entries.Keys.Where(k => !requiredSet.Contains(k)).ToList())
                {
                    _entries[key].Cts?.Cancel();
                    _entries.Remove(key);
                    changed = true;
                }

                var available = _cacheDir != null && TractnniLoader.TryGetPath() != null;
                foreach (var key in required)
                {
                    var source = project.Sources?.FirstOrDefault(s => s.Id == key.SourceId);
                    if (source?.Path == null)
                        continue;

                    if (_entries.TryGetValue(key, out var entry))
                    {
                        // a stuck state whose reason has passed re-enters the queue below: the
                        // binary appeared (Unavailable), or the source file changed under a
                        // finished sidecar (Completed but no longer valid).
                        var stale = (entry.State == AiAnalysisState.Unavailable && available)
                            || (entry.State == AiAnalysisState.Completed && !IsValid(key, source));
                        if (!stale)
                            continue;

                        _entries.Remove(key);
                        changed = true;
                    }

                    if (IsValid(key, source))
                        continue;

                    if (!available)
                    {
                        _entries[key] = new Entry { State = AiAnalysisState.Unavailable, Source = source };
                        changed = true;
                        continue;
                    }

                    _entries[key] = new Entry
                    {
                        State = AiAnalysisState.Queued,
                        Source = source,
                        Cts = new CancellationTokenSource(),
                    };
                    _queue.Enqueue(key);
                    changed = true;
                }

                if (_queue.Count > 0 && !_workerRunning)
                {
                    _workerRunning = true;
                    _ = Task.Run(WorkerLoop);
                }
            }

            if (changed)
                RaiseChanged();
        }

        private bool IsValid(AiSidecarKey key, Source source) =>
            AiSidecars.IsValid(SidecarPath(key), source.Path);

        private string SidecarPath(AiSidecarKey key) => key.Kind == AiSidecarKind.Denoise
            ? AiSidecars.DenoisePath(_cacheDir, key.SourceId, key.StreamIndex)
            : AiSidecars.MattePath(_cacheDir, key.SourceId, key.StreamIndex);

        /// <summary>One job at a time until the queue drains; a Refresh that queues more while a
        /// job runs is picked up by the same loop.</summary>
        private void WorkerLoop()
        {
            while (true)
            {
                AiSidecarKey key;
                Entry entry;
                lock (_sync)
                {
                    if (_disposed || _queue.Count == 0)
                    {
                        _workerRunning = false;
                        return;
                    }

                    key = _queue.Dequeue();
                    // cancelled while queued (setting turned off): the entry is gone.
                    if (!_entries.TryGetValue(key, out entry) || entry.State != AiAnalysisState.Queued)
                        continue;

                    entry.State = AiAnalysisState.Running;
                    entry.Progress = 0;
                }

                RaiseChanged();
                RunJob(key, entry);
            }
        }

        private void RunJob(AiSidecarKey key, Entry entry)
        {
            var state = AiAnalysisState.Failed;
            string failureDetail = null;
            try
            {
                int lastPercent = -1;
                var progress = new Progress<double>(p =>
                {
                    var percent = (int)(Math.Clamp(p, 0, 1) * 100);
                    lock (_sync)
                    {
                        if (_entries.TryGetValue(key, out var live) && ReferenceEquals(live, entry))
                            live.Progress = Math.Clamp(p, 0, 1);
                    }
                    if (percent != Interlocked.Exchange(ref lastPercent, percent))
                        RaiseChanged();
                });

                var generated = key.Kind == AiSidecarKind.Denoise
                    ? DenoiseGenerator.Generate(entry.Source, key.StreamIndex, _cacheDir, progress, entry.Cts.Token)
                    : MatteGenerator.Generate(entry.Source, key.StreamIndex, _cacheDir, progress, entry.Cts.Token);

                // false = no binary resolved after all (it disappeared between the probe and the
                // run) — the unavailable note, not the failure one.
                state = generated ? AiAnalysisState.Completed : AiAnalysisState.Unavailable;
            }
            catch (OperationCanceledException)
            {
                // the requiring setting was turned off (Refresh dropped the entry) or the window
                // closed; either way there is no row left to update.
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AI {key.Kind} generation failed: " + ex);
                SentryConfig.CaptureHandled(ex, "ai.generate");
                failureDetail = ex.Message;
            }

            bool live;
            lock (_sync)
            {
                live = _entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry);
                if (live)
                {
                    entry.State = state;
                    entry.Progress = state == AiAnalysisState.Completed ? 1 : entry.Progress;
                    entry.FailureDetail = failureDetail;
                }
            }

            if (!live)
                return;

            RaiseChanged();
            if (state == AiAnalysisState.Completed)
                RaiseOnUIThread(() => SidecarCompleted?.Invoke(this, EventArgs.Empty));
            else if (state == AiAnalysisState.Failed)
                RaiseOnUIThread(() => JobFailed?.Invoke(this, new AiJobFailedEventArgs(key, failureDetail)));
        }

        private void RaiseChanged() => RaiseOnUIThread(() => Changed?.Invoke(this, EventArgs.Empty));

        private void RaiseOnUIThread(Action raise)
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_sync)
                {
                    if (_disposed)
                        return;
                }
                raise();
            });
        }

        public void Dispose()
        {
            _session.ProjectChanged -= Session_ProjectChanged;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;

                foreach (var entry in _entries.Values)
                    entry.Cts?.Cancel();
                _entries.Clear();
                _queue.Clear();
            }
        }
    }
}
