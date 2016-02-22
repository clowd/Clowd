using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Anotar.NLog;

namespace Clowd.Server.MimeHandlers
{
    public static class MimeHelper
    {
        private static List<KeyValuePair<string, string[]>> _mimeDictionary = null;
        private const string ApacheUrl = "http://svn.apache.org/viewvc/httpd/httpd/trunk/docs/conf/mime.types?view=co";
        public static string GetMimeType(string extension)
        {
            Init();
            extension = extension.TrimStart('.');
            var item = _mimeDictionary.FirstOrDefault(mime => mime.Value.Contains(extension, StringComparer.OrdinalIgnoreCase));
            if (item.Equals(default(KeyValuePair<string, string[]>)))
                return null;
            return item.Key;
        }

        public static string GetDefaultMimeType()
        {
            return "application/octet-stream";
        }

        public static int Init()
        {
            if (_mimeDictionary == null)
            {
                string mimeList;
                try
                {
                    using (var wc = new WebClient())
                    {
                        mimeList = wc.DownloadString(ApacheUrl);
                    }
                }
                catch (WebException ex)
                {
                    if (File.Exists("mime.types"))
                    {
                        LogTo.Warn("Apache mime list unavailable: " + ex.Message);
                        mimeList = File.ReadAllText("mime.types");
                    }
                    else
                    {
                        LogTo.Error("Unable to load mime types: " + ex.Message);
                        return 0;
                    }
                }
                var mimes = from line in mimeList.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            where !line.StartsWith("#") && !String.IsNullOrWhiteSpace(line)
                            let split = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries)
                            let extensions = split[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            where extensions.Any()
                            select new { Mime = split[0], Extensions = extensions };

                _mimeDictionary =
                    mimes.ToDictionary(mime => mime.Mime, mime => mime.Extensions,
                        StringComparer.InvariantCultureIgnoreCase).ToList();
            }
            return _mimeDictionary.Count;
        }
    }
}
