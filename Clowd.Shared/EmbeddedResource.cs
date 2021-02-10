using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    /// <summary>
    /// Provides helper methods for extracting & reading embedded resources. Can be inheirited / instantiated to build strongly typed resource classes. 
    /// Will automatically decompress any gzipped files ending in ".gz".
    /// </summary>
    public class EmbeddedResource
    {
        /// <summary>
        /// Writes the specified resource to the provided output directory. Output directory must already exist. Returns full path of the output/written file.
        /// </summary>
        public static string WriteToFile(string resourceNameSpace, string resourceFileName, string outputDirectory)
        {
            return WriteToFile(resourceNameSpace, resourceFileName, outputDirectory, Assembly.GetCallingAssembly());
        }

        /// <summary>
        /// Writes the specified resource to the provided output directory. Output directory must already exist. Returns full path of the output/written file.
        /// </summary>
        public static string WriteToFile(string resourceNameSpace, string resourceFileName, string outputDirectory, Assembly resourceAssembly)
        {
            return new EmbeddedResource(resourceAssembly, resourceNameSpace).WriteToFile(resourceFileName, outputDirectory);
        }

        /// <summary>
        /// Returns the decompressed stream of the resource.
        /// </summary>
        public static Stream GetStream(string resourceNameSpace, string resourceFileName)
        {
            return GetStream(resourceNameSpace, resourceFileName, Assembly.GetCallingAssembly());
        }

        /// <summary>
        /// Returns the decompressed stream of the resource.
        /// </summary>
        public static Stream GetStream(string resourceNameSpace, string resourceFileName, Assembly resourceAssembly)
        {
            return new EmbeddedResource(resourceAssembly, resourceNameSpace).GetStream(resourceFileName);
        }

        /// <summary>
        /// Returns the original filename and decompressed stream of the specified resouce.
        /// </summary>
        public static (string ResourceName, Stream ResourceStream) GetDetails(string resourceNameSpace, string resourceFileName)
        {
            return GetDetails(resourceNameSpace, resourceFileName, Assembly.GetCallingAssembly());
        }

        /// <summary>
        /// Returns the original filename and decompressed stream of the specified resouce.
        /// </summary>
        public static (string ResourceName, Stream ResourceStream) GetDetails(string resourceNameSpace, string resourceFileName, Assembly resourceAssembly)
        {
            return new EmbeddedResource(resourceAssembly, resourceNameSpace).GetDetails(resourceFileName);
        }

        private readonly Assembly _resourceAssembly;
        private readonly string _resourceNameSpace;

        protected EmbeddedResource(Assembly resourceAssembly, string resourceNameSpace)
        {
            _resourceAssembly = resourceAssembly;
            _resourceNameSpace = resourceNameSpace.TrimEnd().TrimEnd('.') + ".";
        }

        /// <summary>
        /// Writes the specified resource to the provided output directory. Output directory must already exist. Returns full path of the output/written file.
        /// </summary>
        protected string WriteToFile(string resourceFileName, string outputDirectory)
        {
            outputDirectory = Path.GetFullPath(outputDirectory);

            if (!Directory.Exists(outputDirectory))
                throw new DirectoryNotFoundException($"Provided output directory does not exist: \"{outputDirectory}\".");

            var resource = GetDetails(resourceFileName);

            var path = Path.Combine(outputDirectory, resource.ResourceName);

            using (var stream = resource.ResourceStream)
            using (FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[65536];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    fileStream.Write(buffer, 0, count);
            }

            return path;
        }

        /// <summary>
        /// Returns the stream of the resource (namespace and filename).
        /// </summary>
        protected Stream GetStream(string resourceFileName)
        {
            return GetDetails(resourceFileName).ResourceStream;
        }

        /// <summary>
        /// Returns the original filename and decompressed stream of the specified resouce.
        /// </summary>
        protected (string ResourceName, Stream ResourceStream) GetDetails(string resourceFileName)
        {
            string[] manifestResourceNames = _resourceAssembly.GetManifestResourceNames();

            var resourcePath = _resourceNameSpace + resourceFileName;

            // look for precise match
            var name = manifestResourceNames.SingleOrDefault(n => n.Equals(resourcePath, StringComparison.OrdinalIgnoreCase));

            // look for a gzipped resource
            if (name == null)
                name = manifestResourceNames.SingleOrDefault(n => n.Equals(resourcePath + ".gz", StringComparison.OrdinalIgnoreCase));

            if (name == null)
                throw new FileNotFoundException($"Unable to locate resource \"{resourcePath}\" in assembly \"{_resourceAssembly.GetName()}\". Please verify the file and namespace spelling, and check that the build action of file is set to Embedded Resource.");

            var filename = name.Substring(_resourceNameSpace.Length);
            var stream = _resourceAssembly.GetManifestResourceStream(name);

            if (filename.EndsWith(".gz"))
            {
                stream = new GZipStream(stream, CompressionMode.Decompress, false);
                filename = filename.Substring(0, filename.Length - 3);
            }

            return (filename, stream);
        }
    }
}
