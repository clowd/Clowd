using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clowd.PlatformUtil;
using Newtonsoft.Json;

namespace Clowd
{
    public class SessionInfo
    {
        public DateTime Created;
        public string RootPath;
        public string CroppedPath;
        public string DesktopPath;
        public ScreenRect SelectionRect;
        public SessionWindow[] Windows;

        public void Save()
        {
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
            foreach(var d in Directory.EnumerateDirectories(PathConstants.SessionData))
            {
                var p = Path.Combine(d, "session.json");
                if (File.Exists(p))
                    yield return Parse(p);
            }
        }
    }
}
