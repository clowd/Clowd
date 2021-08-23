using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI;
using Clowd.UI.Helpers;
using Clowd.Util;
using ModernWpf.Controls;
using Newtonsoft.Json;
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

    public class SessionInfo : FileSyncObject
    {
        public SessionInfo(string file) : base(file)
        {
        }

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

        public ScreenRect CroppedRect
        {
            get => Get<ScreenRect>();
            set => Set(value);
        }

        public SessionWindow[] Windows
        {
            get => Get<SessionWindow[]>();
            set => Set(value);
        }

        public string ActiveWindowId
        {
            get => Get<string>();
            set => Set(value);
        }

        public string Name
        {
            get => Get<string>();
            set => Set(value);
        }

        public Symbol Icon
        {
            get => Get<Symbol>();
            set => Set(value);
        }

        public string GraphicsStream
        {
            get => Get<string>();
            set => Set(value);
        }
    }

    public class SessionManager : IDisposable, INotifyPropertyChanged
    {
        public TrulyObservableCollection<SessionInfo> Sessions { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public static SessionManager Current { get; }

        private static readonly object _lock = new object();

        static SessionManager()
        {
            Current = new SessionManager();
        }

        private FileSystemWatcher _fsw;
        private IDisposable _cleanupTimer;

        public SessionManager()
        {
            Sessions = new TrulyObservableCollection<SessionInfo>();
            foreach (var d in Directory.EnumerateDirectories(PathConstants.SessionData))
            {
                var jsonPath = Path.Combine(d, "session.json");
                if (File.Exists(jsonPath))
                    Sessions.Add(new SessionInfo(jsonPath));
            }

            _fsw = new FileSystemWatcher(PathConstants.SessionData);
            _fsw.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName;
            _fsw.EnableRaisingEvents = true;
            _fsw.Created += new FileSystemEventHandler(SynchronizationContextEventHandler.CreateDelegate<FileSystemEventArgs>(OnCreated));
            _fsw.Deleted += new FileSystemEventHandler(SynchronizationContextEventHandler.CreateDelegate<FileSystemEventArgs>(OnDeleted));

            OnCleanUpTimerTick();
            _cleanupTimer = DisposableTimer.Start(TimeSpan.FromDays(1), OnCleanUpTimerTick);
        }

        private void OnCleanUpTimerTick()
        {
            var deleteSessionsAfter = SettingsRoot.Current.Editor.DeleteSessionsAfter.ToTimeSpan();
            foreach(var s in Sessions.ToArray())
            {
                var sAge = DateTime.UtcNow - s.LastModifiedUtc;
                if (sAge > deleteSessionsAfter && s.ActiveWindowId == null)
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
            if (session.ActiveWindowId != null)
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

            var data = new ClipboardDataObject();
            data.SetImage(bi);
            data.SetClipboardData();
        }

        public string CreateNewSessionDirectory()
        {
            var dir = PathConstants.GetDatedFilePath("session", "0", PathConstants.SessionData);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public SessionInfo CreateNewSession()
        {
            var jsonPath = Path.Combine(CreateNewSessionDirectory(), "session.json");
            var session = new SessionInfo(jsonPath);
            session.Name = "Document";
            session.Icon = Symbol.Document;
            session.CreatedUtc = DateTime.UtcNow;
            Sessions.Add(session);
            return session;
        }
    }
}
