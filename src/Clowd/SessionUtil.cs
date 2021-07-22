using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Clowd.PlatformUtil;
using Clowd.UI;
using Clowd.Util;
using ModernWpf.Controls;
using Newtonsoft.Json;
using WeakEvent;

namespace Clowd
{
    public class SessionInfo : INotifyPropertyChanged
    {
        public string TimeAgo => Created == default ? "Unknown" : PrettyTime.Format(Created - DateTime.UtcNow);
        public DateTime Created { get; init; }
        public string RootPath { get; init; }
        public string CroppedPath { get; init; }
        public string DesktopPath { get; init; }
        public ScreenRect SelectionRect { get; init; }
        public SessionWindow[] Windows { get; init; }

        public string ActiveWindowId { get; set; }
        public string Name { get; set; } = "Document";
        public Symbol Icon { get; set; } = Symbol.Document;
        public DateTime LastModified { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public void Save()
        {
            if (String.IsNullOrEmpty(RootPath))
                throw new InvalidOperationException("RootPath can not be null");
            LastModified = DateTime.UtcNow;
            var json = JsonConvert.SerializeObject(this);
            File.WriteAllText(Path.Combine(RootPath, "session.json"), json);
        }

        public void Delete()
        {
            if (ActiveWindowId != null)
                throw new InvalidOperationException("Can't delete session that is opened in an editor");
        }

        public void Open()
        {
            EditorWindow.ShowSession(this);
        }

        public void Copy()
        {

        }
    }

    public struct SessionWindow
    {
        public string Caption;
        public string Class;
        public string FilePath;
        public bool Selected;
        public int Id;
        public ScreenRect Position;
    }

    static class SessionUtil
    {
        public static event EventHandler<EventArgs> WeakSessionsUpdated
        {
            add { _myEventSource.Subscribe(value); }
            remove { _myEventSource.Unsubscribe(value); }
        }

        static readonly WeakEventSource<EventArgs> _myEventSource = new WeakEventSource<EventArgs>();
        static FileSystemWatcher _fsw;
        static SessionUtil()
        {
            _fsw = new FileSystemWatcher(PathConstants.SessionData);
            _fsw.IncludeSubdirectories = true;
            _fsw.EnableRaisingEvents = true;
            _fsw.Changed += _myEventSource.Raise;
            _fsw.Created += _myEventSource.Raise;
            _fsw.Deleted += _myEventSource.Raise;
            _fsw.Renamed += _myEventSource.Raise;
        }

        public static string CreateNewSessionDirectory()
        {
            var dir = PathConstants.GetDatedFilePath("session", "0", PathConstants.SessionData);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static SessionInfo CreateNewSession()
        {
            var session = new SessionInfo
            {
                RootPath = CreateNewSessionDirectory(),
                Created = DateTime.UtcNow,
            };
            session.Save();
            return session;
        }

        public static SessionInfo Parse(string sessionPath)
        {
            if (sessionPath == null)
                return null;

            return JsonConvert.DeserializeObject<SessionInfo>(File.ReadAllText(sessionPath));
        }

        public static IEnumerable<SessionInfo> GetSavedSessions()
        {
            foreach (var d in Directory.EnumerateDirectories(PathConstants.SessionData))
            {
                var p = Path.Combine(d, "session.json");
                if (File.Exists(p))
                    yield return Parse(p);
            }
        }
    }
}
