using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace PhotoLoader.ImageLoaders
{
    class ExternalLoader : ILoader
    {
        public static Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        public System.IO.Stream Load(string source)
        {
            if (_cache.ContainsKey(source))
                return new FileStream(_cache[source], FileMode.Open);

            var webClient = new WebClient();
            byte[] html = webClient.DownloadData(source);

            if (html == null || html.Count() == 0) return null;
            var tmp = Path.GetTempFileName();
            File.WriteAllBytes(tmp, html);
            _cache.Add(source, tmp);
            return new MemoryStream(html);
        }
    }
}