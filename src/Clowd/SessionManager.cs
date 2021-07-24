using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
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

        public string CroppedImgPath
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
    }

    public class SessionManager : IDisposable, INotifyPropertyChanged
    {
        public TrulyObservableCollection<SessionInfo> Sessions { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public static SessionManager Current { get; }

        static SessionManager()
        {
            Current = new SessionManager();
        }

        private FileSystemWatcher _fsw;

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
        }

        ~SessionManager()
        {
            _fsw.Dispose();
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
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

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            var jsonPath = Path.Combine(e.FullPath, "session.json");
            if (File.Exists(jsonPath) && !FileSyncObject.CheckPathInUse(jsonPath))
                Sessions.Add(new SessionInfo(jsonPath));
            else if (e.FullPath.EndsWith("session.json", StringComparison.OrdinalIgnoreCase) && !FileSyncObject.CheckPathInUse(e.FullPath))
                Sessions.Add(new SessionInfo(e.FullPath));
        }

        public void Dispose()
        {
            _fsw.Dispose();
        }

        public SessionInfo GetSessionFromPath(string path)
        {
            return Sessions.FirstOrDefault(s => s.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                ?? Sessions.FirstOrDefault(s => s.FilePath.Equals(Path.Combine(path, "session.json"), StringComparison.OrdinalIgnoreCase));
        }

        public void OpenSession(SessionInfo session)
        {
            EditorWindow.ShowSession(session);
        }

        public void DeleteSession(SessionInfo session)
        {
            if (session.ActiveWindowId != null)
                throw new InvalidOperationException("Can't delete session that is opened in an editor");
        }

        public void CopySession(SessionInfo session)
        {

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
