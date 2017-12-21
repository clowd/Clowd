using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace FileUploadLib
{
    public class MimeDbMimeProvider : IMimeProvider
    {
        // the following url is locked to a specific commit, so this should be updated periodically.
        private const string mimePath = "https://raw.githubusercontent.com/jshttp/mime-db/63731b4b161970d3205dda268102a44ea9436254/db.json";

        private static Dictionary<string, MimeDbMimeEntry> _database;
        private static DateTime _databaseRefreshTime;
        private static readonly object _databaseLock = new object();

        private static void EnsureMimeCache()
        {
            lock (_databaseLock)
            {
                if (_database != null)
                    return;

                using (WebClient wc = new WebClient())
                {
                    var txt = wc.DownloadString(mimePath);
                    var obj = JsonConvert.DeserializeObject<Dictionary<string, MimeDbMimeEntry>>(txt);
                    foreach (var kvp in obj)
                    {
                        kvp.Value.ContentType = kvp.Key;
                        kvp.Value.Extensions = kvp.Value.Extensions ?? new string[0];
                    }

                    _database = obj;
                    _databaseRefreshTime = DateTime.Now;
                }
            }
        }

        public IMimeEntry GetMimeFromExtension(string extension)
        {
            EnsureMimeCache();
            extension = extension.ToLower().Trim('.');
            return _database.Values.FirstOrDefault(o => o.Extensions.Contains(extension)) ?? GetDefaultDownloadMime();
        }

        public IMimeEntry GetDefaultDownloadMime()
        {
            EnsureMimeCache();
            return _database["application/octet-stream"];
        }
    }

    public interface IMimeProvider
    {
        IMimeEntry GetMimeFromExtension(string extension);
        IMimeEntry GetDefaultDownloadMime();
    }

    public class MimeDbMimeEntry : IMimeEntry
    {
        public string ContentType { get; set; }
        public string Source { get; set; }
        public string[] Extensions { get; set; }
        public bool? Compressible { get; set; }
        public string Charset { get; set; }
    }

    public interface IMimeEntry
    {
        string ContentType { get; set; }
        string Source { get; set; }
        string[] Extensions { get; set; }
        bool? Compressible { get; set; }
        string Charset { get; set; }
    }
}
