using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clowd.Clipboard;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI;
using Clowd.UI.Helpers;
using Clowd.Util;
using Newtonsoft.Json;
using NLog;
using WeakEvent;

namespace Clowd
{
    public class SessionWindow
    {
        public string Caption { get; init; }
        public string Class { get; init; }
        public string ImgPath { get; init; }
        public bool Selected { get; init; }
        public int Id { get; init; }
        public ScreenRect Position { get; init; }
    }

    public record SessionOpenEditor
    {
        public Guid? VirtualDesktopId { get; init; }
        public bool IsTopMost { get; init; }
        public ScreenRect Position { get; init; }
    }

    public class SessionInfo : FileSyncObject
    {
        public SessionInfo(string file) : base(file)
        { }

        public DateTime CreatedUtc
        {
            get => Get<DateTime>();
            set => Set(value);
        }

        public string PreviewImgPath
        {
            get => Get<string>();
            set => Set(value);
        }

        public string DesktopImgPath
        {
            get => Get<string>();
            set => Set(value);
        }

        public string CursorImgPath
        {
            get => Get<string>();
            set => Set(value);
        }

        public ScreenRect CursorPosition
        {
            get => Get<ScreenRect>();
            set => Set(value);
        }

        public Color CanvasBackground
        {
            get => Get<Color>();
            set => Set(value);
        }

        public ScreenRect CroppedRect
        {
            get => Get<ScreenRect>();
            set => Set(value);
        }

        public ScreenRect OriginalBounds
        {
            get => Get<ScreenRect>();
            set => Set(value);
        }

        public SessionOpenEditor OpenEditor
        {
            get => Get<SessionOpenEditor>();
            set => Set(value);
        }

        public SessionWindow[] Windows
        {
            get => Get<SessionWindow[]>();
            set => Set(value);
        }

        public string Name
        {
            get => Get<string>();
            set => Set(value);
        }

        [Obsolete]
        public string GraphicsStream
        {
            get => Get<string>();
            //set => Set(value);
        }

        public string UploadFileKey
        {
            get => Get<string>();
            set => Set(value);
        }

        public string UploadUrl
        {
            get => Get<string>();
            set => Set(value);
        }

        // this does not need to be persisted
        public double UploadProgress
        {
            get => _uploadProgress;
            set
            {
                _uploadProgress = value;
                OnPropertyChanged();
            }
        }

        private double _uploadProgress;
    }

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
        private ILogger _log = LogManager.GetCurrentClassLogger();

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
                    _log.Warn(e, "Unable to load session: " + jsonPath);
                }
            }

            _fsw = new FileSystemWatcher(PathConstants.SessionData);
            _fsw.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName;
            _fsw.EnableRaisingEvents = true;
            _fsw.Created += new FileSystemEventHandler(SynchronizationContextEventHandler.CreateDelegate<FileSystemEventArgs>(OnCreated));
            _fsw.Deleted += new FileSystemEventHandler(SynchronizationContextEventHandler.CreateDelegate<FileSystemEventArgs>(OnDeleted));

            OnCleanUpTimerTick();
            _cleanupTimer = DisposableTimer.Start(TimeSpan.FromHours(1), OnCleanUpTimerTick);
        }

        private void OnCleanUpTimerTick()
        {
            var deleteSessionsAfter = SettingsRoot.Current.Editor.DeleteSessionsAfter.ToTimeSpan();
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
            BitmapImage bi = new BitmapImage();
            bi.LoadCachedUri(session.PreviewImgPath);
            ClipboardWpf.SetImage(bi);
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
            session.CanvasBackground = SettingsRoot.Current.Editor.CanvasBackground;
            Sessions.Add(session);
            return session;
        }
    }
}
