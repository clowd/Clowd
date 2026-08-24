using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI;
using Clowd.UI.Helpers;
using Clowd.Util;

namespace Clowd
{
    public class SessionManager : SimpleNotifyObject, IDisposable
    {
        public TrulyObservableCollection<SessionInfo> Sessions
        {
            get => _sessions;
            set => Set(ref _sessions, value);
        }

        public static SessionManager Current { get; }

        /// <summary>The session this app instance created most recently, or null once it has been
        /// deleted. The Recent page selects it, so a capture, recording or upload that opens the page
        /// lands on the entry it just made. Always assigned after the session is in
        /// <see cref="Sessions"/>, so a listener reacting to the change can already find it there —
        /// which is why this, and not the collection's own Add, is the signal the page listens to.</summary>
        public SessionInfo LastCreated
        {
            get => _lastCreated;
            private set => Set(ref _lastCreated, value);
        }

        private static readonly object _lock = new object();

        static SessionManager()
        {
            Current = new SessionManager();
        }

        private FileSystemWatcher _fsw;
        private IDisposable _cleanupTimer;
        private TrulyObservableCollection<SessionInfo> _sessions;
        private SessionInfo _lastCreated;

        private SessionManager()
        {
            Sessions = new TrulyObservableCollection<SessionInfo>();
            foreach (var d in Directory.EnumerateDirectories(PathConstants.SessionData))
            {
                var jsonPath = Path.Combine(d, "session.json");
                try
                {
                    if (File.Exists(jsonPath))
                        Sessions.Add(new SessionInfo(jsonPath));
                }
                catch (Exception e)
                {
                    Debug.WriteLine("Unable to load session: " + jsonPath + Environment.NewLine + e);
                    SentryConfig.CaptureHandled(e, "session.load");
                }
            }

            _fsw = new FileSystemWatcher(PathConstants.SessionData);
            _fsw.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName;
            _fsw.EnableRaisingEvents = true;
            _fsw.Created += (s, e) => Dispatcher.UIThread.Post(() => OnCreated(s, e));
            _fsw.Deleted += (s, e) => Dispatcher.UIThread.Post(() => OnDeleted(s, e));

            OnCleanUpTimerTick();
            _cleanupTimer = DisposableTimer.Start(TimeSpan.FromHours(1), OnCleanUpTimerTick);
        }

        private void OnCleanUpTimerTick()
        {
            var deleteAfterOption = SettingsRoot.Current?.Editor?.DeleteSessionsAfter;
            if (deleteAfterOption == null)
                return;

            var deleteSessionsAfter = deleteAfterOption.ToTimeSpan();
            var sessions = Sessions.ToArray();

            // a star means "keep this", and it keeps the whole chain the starred entry sits on:
            // sweeping away the project a starred render came out of, or the recording a starred
            // GIF was made from, would leave the thing the user actually starred stranded with its
            // history gone. Computed over every session, before any of them is deleted.
            var starred = SessionLinks.CollectStarredChains(sessions);

            foreach (var s in sessions)
            {
                if (starred.Contains(s))
                    continue;

                var sAge = DateTime.UtcNow - s.LastModifiedUtc;
                if (sAge > deleteSessionsAfter && s.OpenEditor == null)
                    DeleteSession(s);
            }
        }

        ~SessionManager()
        {
            _fsw.Dispose();
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            lock (_lock)
            {
                foreach (var s in Sessions.ToArray())
                {
                    if (s.FilePath.StartsWith(e.FullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Sessions.Remove(s);
                        s.Dispose();
                    }
                }
            }
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            GetSessionFromPath(e.FullPath); // will cause to be loaded if not already
        }

        public void Dispose()
        {
            _fsw.Dispose();
        }

        public SessionInfo GetSessionFromPath(string path)
        {
            lock (_lock)
            {
                var inmem = Sessions.FirstOrDefault(s => s.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                            ?? Sessions.FirstOrDefault(s => s.FilePath.Equals(Path.Combine(path, "session.json"), StringComparison.OrdinalIgnoreCase));

                if (inmem != null)
                    return inmem;

                SessionInfo loaded = null;

                try
                {
                    var jsonPath = Path.Combine(path, "session.json");
                    if (File.Exists(jsonPath) && !FileSyncObject.CheckPathInUse(jsonPath))
                        loaded = new SessionInfo(jsonPath);
                    else if (path.EndsWith("session.json", StringComparison.OrdinalIgnoreCase) && !FileSyncObject.CheckPathInUse(path))
                        loaded = new SessionInfo(path);
                }
                catch (InvalidDataException e)
                {
                    // a corrupt session.json (quarantined to .corrupt by FileSyncObject) is simply
                    // not shown; callers already handle a null return. Only that deliberate
                    // outcome is swallowed — a transient failure propagating here is better than
                    // making a finished capture silently look canceled.
                    Debug.WriteLine("Unable to load session: " + path + Environment.NewLine + e);
                    SentryConfig.CaptureHandled(e, "session.load");
                }

                if (loaded != null)
                    Sessions.Add(loaded);

                return loaded;
            }
        }

        public void OpenSession(SessionInfo session)
        {
            EditorWindow.ShowSession(session);
        }

        public void DeleteSession(SessionInfo session)
        {
            if (session.OpenEditor != null)
                throw new InvalidOperationException("Can't delete session that is opened in an editor");

            lock (_lock)
            {
                Sessions.Remove(session);
                if (ReferenceEquals(LastCreated, session))
                    LastCreated = null;
                session.Dispose();
                Directory.Delete(Path.GetDirectoryName(session.FilePath), true);
            }
        }

        public void CopySession(SessionInfo session)
        {
            var path = session?.PreviewImgPath;
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            _ = ClipboardImpl.SetClipboardImage(Toast.GetPrimaryClipboard(), File.ReadAllBytes(path));
        }

        public string GetNextSessionDirectory()
        {
            return PathConstants.GetDatedFilePath("session", "0", PathConstants.SessionData);
        }

        public SessionInfo CreateNewSession()
        {
            var dir = GetNextSessionDirectory();
            Directory.CreateDirectory(dir);
            var jsonPath = Path.Combine(dir, "session.json");
            var session = new SessionInfo(jsonPath);
            session.Name = "Document";
            session.CreatedUtc = DateTime.UtcNow;
            Sessions.Add(session);
            LastCreated = session;
            return session;
        }

        /// <summary>Creates a session backed by an already-existing directory (e.g. a video
        /// recording dir the capturer pre-populated with cropped.png). Identical to
        /// <see cref="CreateNewSession"/> except the directory is not created, and a session the
        /// FileSystemWatcher may have already registered for this dir is reused rather than
        /// duplicated.</summary>
        public SessionInfo CreateSessionInDirectory(string dir)
        {
            lock (_lock)
            {
                var jsonPath = Path.Combine(dir, "session.json");

                var existing = Sessions.FirstOrDefault(s => s.FilePath.Equals(jsonPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    // the watcher got here first — this is still the session the caller is creating.
                    LastCreated = existing;
                    return existing;
                }

                var session = new SessionInfo(jsonPath);
                session.Name = "Document";
                session.CreatedUtc = DateTime.UtcNow;
                Sessions.Add(session);
                LastCreated = session;
                return session;
            }
        }
    }
}
