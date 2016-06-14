using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Clowd.Shared
{
    public static class MimeTable
    {
        private static MimeType[] _cachedTypes;

        public static IEnumerable<MimeType> LookupExt(string extension)
        {
            EnsureCache();
            extension = extension.TrimStart('.');
            return _cachedTypes.Where(type => type.extensions?.Contains(extension, StringComparer.OrdinalIgnoreCase) == true);
        }

        public static MimeType LookupName(string mime)
        {
            EnsureCache();
            return _cachedTypes.FirstOrDefault(m => m.name.Equals(mime, StringComparison.OrdinalIgnoreCase));
        }

        public static MimeType[] GetAll()
        {
            return _cachedTypes;
        }

        private static void EnsureCache()
        {
            if (_cachedTypes != null && _cachedTypes.Length > 0)
                return;

            var json = GetEmbeddedJson();
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            var dict = serializer.Deserialize<Dictionary<string, MimeType>>(json);

            List<MimeType> list = new List<MimeType>(dict.Count);
            foreach (var item in dict)
            {
                var mime = item.Value;
                mime.name = item.Key;
                list.Add(mime);
            }

            _cachedTypes = list.ToArray();
        }

        private static string GetEmbeddedJson()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                    .First(name => name.EndsWith("mime-db.json", StringComparison.OrdinalIgnoreCase));

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }

    public struct MimeType
    {
        public string name;
        public string source;
        public string charset;
        public bool? compressible;
        public string[] extensions;

        public override string ToString()
        {
            return name;
        }
    }
}
