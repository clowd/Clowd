using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

        private static readonly object _lock = new object();

        static SessionManager()
        {
            Current = new SessionManager();
        }

        private FileSystemWatcher _fsw;
        private IDisposable _cleanupTimer;
        private TrulyObservableCollection<SessionInfo> _sessions;

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
            foreach (var s in Sessions.ToArray())
            {
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

                var jsonPath = Path.Combine(path, "session.json");
                if (File.Exists(jsonPath) && !FileSyncObject.CheckPathInUse(jsonPath))
                    loaded = new SessionInfo(jsonPath);
                else if (path.EndsWith("session.json", StringComparison.OrdinalIgnoreCase) && !FileSyncObject.CheckPathInUse(path))
                    loaded = new SessionInfo(path);

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
                session.Dispose();
                Directory.Delete(Path.GetDirectoryName(session.FilePath), true);
            }
        }

        public void CopySession(SessionInfo session)
        {
            var path = session?.PreviewImgPath;
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            _ = ClipboardImpl.SetClipboardImage(GetClipboard(), File.ReadAllBytes(path));
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
            return session;
        }

        private static IClipboard GetClipboard()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = desktop.MainWindow
                             ?? desktop.Windows.FirstOrDefault(w => w.IsActive)
                             ?? desktop.Windows.FirstOrDefault();
                return window?.Clipboard;
            }

            return null;
        }
    }
}
