using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Clowd.PlatformUtil;
using ModernWpf.Controls;
using Newtonsoft.Json;

namespace Clowd
{
    public class SessionInfo : INotifyPropertyChanged
    {
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

        public SessionInfo()
        {
            PropertyChanged += SessionInfo_PropertyChanged;
        }

        private void SessionInfo_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SaveSafe();
        }

        public void Save()
        {
            if (String.IsNullOrEmpty(RootPath))
                throw new InvalidOperationException("RootPath can not be null");
            SaveSafe();
        }

        private void SaveSafe()
        {
            if (String.IsNullOrEmpty(RootPath))
                return;

            LastModified = DateTime.UtcNow;
            var json = JsonConvert.SerializeObject(this);
            File.WriteAllText(Path.Combine(RootPath, "session.json"), json);
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
        public static string CreateNewSessionDirectory()
        {
            var dir = PathConstants.GetDatedFilePath("session", "0", PathConstants.SessionData);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static SessionInfo CreateNewSession()
        {
            return new SessionInfo
            {
                RootPath = CreateNewSessionDirectory()
            };
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
