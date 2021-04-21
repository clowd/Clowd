using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer
{
    class ResourcesEx
    {
        public static string WriteResourceToFile(string resourceName, string directory)
        {
            Assembly executingAssembly = Assembly.GetExecutingAssembly();
            string[] manifestResourceNames = executingAssembly.GetManifestResourceNames();

            var prefix = "Clowd.Installer.Embed.";

            var name = manifestResourceNames.Single(n => n.StartsWith(prefix + resourceName));

            var filename = name.Substring(prefix.Length);
            var stream = executingAssembly.GetManifestResourceStream(name);

            if (filename.EndsWith(".gz"))
            {
                stream = new GZipStream(stream, CompressionMode.Decompress, false);
                filename = filename.Substring(0, filename.Length - 3);
            }

            var path = Path.Combine(directory, filename);

            using (stream)
            using (FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[65536];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    fileStream.Write(buffer, 0, count);
            }

            return path;
        }
    }
}
