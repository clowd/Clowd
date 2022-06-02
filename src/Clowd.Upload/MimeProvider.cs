using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RT.Util.ExtensionMethods;
using YamlDotNet.Serialization;

namespace Clowd.Upload
{
    public class MimeProvider : IMimeProvider
    {
        private static Dictionary<string, MimeDbMimeEntry> _database;
        private static Dictionary<string, LanguageEntry> _languages;
        private static readonly object _lock = new object();

        public MimeProvider()
        {
            EnsureMimeCache();
        }

        private static void EnsureMimeCache()
        {
            // no point acquiring a lock if we know everything is loaded already
            if (_database != null && _languages != null)
                return;

            lock (_lock)
            {
                if (_database != null && _languages != null)
                    return;

                var res = new Resource();

                var deserializer = new DeserializerBuilder()
                    .IgnoreUnmatchedProperties()
                    .Build();
                _languages = deserializer.Deserialize<Dictionary<string, LanguageEntry>>(new System.IO.StreamReader(res.LanguageDb));

                var mimetxt = res.MimeDb.ReadAllText();
                var mimedb = JsonConvert.DeserializeObject<Dictionary<string, MimeDbMimeEntry>>(mimetxt);
                foreach (var kvp in mimedb)
                {
                    kvp.Value.ContentType = kvp.Key;
                    kvp.Value.Extensions = kvp.Value.Extensions ?? new string[0];
                }
                _database = mimedb;
            }

            // the following url is locked to a specific commit, so this should be updated periodically.
            //const string mimePath = "https://raw.githubusercontent.com/jshttp/mime-db/63731b4b161970d3205dda268102a44ea9436254/db.json";
            //using (WebClient wc = new WebClient())
            //{
            //    var txt = await wc.DownloadStringTaskAsync(mimePath);
            //    var obj = JsonConvert.DeserializeObject<Dictionary<string, MimeDbMimeEntry>>(txt);
            //    foreach (var kvp in obj)
            //    {
            //        kvp.Value.ContentType = kvp.Key;
            //        kvp.Value.Extensions = kvp.Value.Extensions ?? new string[0];
            //        kvp.Value.Category = GetCategory(kvp.Value);
            //    }

            //    _database = obj;
            //}
        }

        public IMimeEntry GetMimeFromExtension(string extension)
        {
            extension = extension.ToLower().Trim('.');
            return _database.Values.FirstOrDefault(o => o.Extensions.Contains(extension)) ?? GetDefaultDownloadMime();
        }

        public IMimeEntry GetDefaultDownloadMime()
        {
            return _database["application/octet-stream"];
        }

        public IEnumerable<IMimeEntry> GetMimeEntries()
        {
            return _database.Values.Cast<IMimeEntry>();
        }

        public ContentCategory GetCategoryFromMime(IMimeEntry entry)
        {
            bool starts(string value) => entry.ContentType.StartsWith(value, StringComparison.OrdinalIgnoreCase);
            bool ends(string value) => entry.ContentType.EndsWith(value, StringComparison.OrdinalIgnoreCase);
            bool contains(string value) => entry.ContentType.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
            bool module(string value) => ends("+" + value) || contains("/" + value);
            //bool equals(string value) => entry.ContentType.Equals(value, StringComparison.OrdinalIgnoreCase);

            // image
            if (starts("image/")) return ContentCategory.Image;

            // audio
            if (starts("audio/")) return ContentCategory.Audio;

            // video
            if (starts("video/")) return ContentCategory.Video;

            // text
            if (starts("text/")) return ContentCategory.Text;
            if (module("xml")) return ContentCategory.Text;
            if (module("json")) return ContentCategory.Text;

            // compressed
            if (module("zip")) return ContentCategory.Compressed;
            if (module("gzip")) return ContentCategory.Compressed;
            if (ends("-compressed")) return ContentCategory.Compressed;

            var lang_match = _languages.Values.FirstOrDefault(l => l?.codemirror_mime_type?.Equals(entry.ContentType, StringComparison.OrdinalIgnoreCase) == true);
            if (lang_match != null)
                return GetCategoryFromLanguage(lang_match);

            return ContentCategory.Unknown;
        }

        public ContentCategory GetCategoryFromExtension(string extension)
        {
            extension = extension.ToLower().Trim('.');

            var mime = GetMimeFromExtension(extension);
            var cat = GetCategoryFromMime(mime);
            if (cat != ContentCategory.Unknown)
                return cat;

            var lang_match = _languages.Values.FirstOrDefault(l => l.extensions?.Contains("." + extension) == true);
            if (lang_match != null)
                return GetCategoryFromLanguage(lang_match);

            return ContentCategory.Unknown;
        }

        private ContentCategory GetCategoryFromLanguage(LanguageEntry language)
        {
            // we kind of know that 99.9% of everything in this database is going to parse as text just fine.
            return ContentCategory.Text;
        }

        private class MimeDbMimeEntry : IMimeEntry
        {
            public string ContentType { get; set; }
            public string Source { get; set; }
            public string[] Extensions { get; set; }
            public bool? Compressible { get; set; }
            public string Charset { get; set; }
        }

        private class LanguageEntry
        {
            public string fs_name { get; set; }
            public string type { get; set; }
            public string[] aliases { get; set; }
            public string ace_mode { get; set; }
            public string codemirror_mode { get; set; }
            public string codemirror_mime_type { get; set; }
            public bool wrap { get; set; }
            public string[] extensions { get; set; }
            public string[] filenames { get; set; }
            public string[] interpreters { get; set; }
            public bool searchable { get; set; }
            public int language_id { get; set; }
            public string color { get; set; }
            public string tm_scope { get; set; }
            public string group { get; set; }
        }
    }

    public interface IMimeProvider
    {
        IMimeEntry GetMimeFromExtension(string extension);
        IMimeEntry GetDefaultDownloadMime();
        ContentCategory GetCategoryFromExtension(string extension);
    }

    public enum ContentCategory
    {
        Unknown = 0,
        Image = 2,
        Audio = 3,
        Text = 4,
        Video = 5,
        Compressed = 6,
    }

    public interface IMimeEntry
    {
        string ContentType { get; }
        string Source { get; }
        string[] Extensions { get; }
        bool? Compressible { get; }
        string Charset { get; }
    }
}
