using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Clowd.Util
{
    public class PathEx
    {
        public static string NormalizeFilepath(string filepath)
        {
            string result = System.IO.Path.GetFullPath(filepath);
            result = result.TrimEnd(new[] { '\\' });
            return result;
        }

        public static string GetRelativePath(string rootPath, string fullPath)
        {
            rootPath = NormalizeFilepath(rootPath);
            fullPath = NormalizeFilepath(fullPath);

            if (!fullPath.StartsWith(rootPath))
                throw new Exception("Could not find rootPath in fullPath when calculating relative path.");

            return "." + fullPath.Substring(rootPath.Length);
        }

        public static string FindCommonRoot(IEnumerable<string> paths)
        {
            var pathList = paths.ToList();

            // find the most commonly used separator in the list of paths.
            int forward = 0;
            int backward = 0;
            foreach (var str in pathList)
            {
                foreach (var c in str)
                {
                    if (c.Equals('/'))
                        forward++;
                    else if (c.Equals('\\'))
                        backward++;
                }
            }

            return FindCommonRoot(pathList, forward > backward ? '/' : '\\');
        }

        public static string FindCommonRoot(IEnumerable<string> paths, char separator)
        {
            var pathList = paths.Where(p => !String.IsNullOrEmpty(p)).ToList();

            // find the longest string in the list and separate it into its parts.
            var longest = pathList.Aggregate(string.Empty, (seed, f) => f.Length > seed.Length ? f : seed);
            var separated = longest.Split(new[] { '\\', '/' });

            string common = "";

            foreach (var segment in separated)
            {
                // this is an empty segment, such as between the slashes in "http://"
                if (String.IsNullOrEmpty(segment))
                {
                    common += separator;
                    continue;
                }

                // this is the first segment and its shared by all the paths.
                if (common.Length == 0 && pathList.All(str => str.StartsWith(segment)))
                {
                    common = segment;
                    continue;
                }

                // this is NOT the first segment, but it is shared by all paths.
                var combined = common + separator + segment;
                if (pathList.All(str => str.StartsWith(combined)))
                    common = combined;

                // this segment is not shared by all paths, so we have the common root.
                else break;
            }

            if (!common.EndsWith(separator.ToString()) && pathList.All(p => p.Length > common.Length))
                common += separator;

            return common;
        }

        public static IEnumerable<string> EnumeratePathPattern(string pattern)
        {
            char separator = Path.DirectorySeparatorChar;
            string[] split = pattern.Split('\\', '/');

            if (split[0].Contains('*') || split[0].Contains('?'))
                throw new ArgumentException("path root must not have a wildcard", nameof(split));

            Func<string[], string, IEnumerable<string>> matchInternal = null;
            matchInternal = (parts, root) =>
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    // if this part of the path is a wildcard that needs expanding
                    if (parts[i].Contains('*') || parts[i].Contains('?'))
                    {
                        // create an absolute path up to the current wildcard and check if it exists
                        var combined = root + separator + String.Join(separator.ToString(), parts.Take(i));
                        if (!Directory.Exists(combined))
                            return new string[0];

                        if (i == parts.Length - 1) // if this is the end of the path (a file name)
                        {
                            return Directory.EnumerateFiles(combined, parts[i], SearchOption.TopDirectoryOnly);
                        }
                        else // if this is in the middle of the path (a directory name)
                        {
                            var directories = Directory.EnumerateDirectories(combined, parts[i],
                                SearchOption.TopDirectoryOnly);
                            var paths = directories.SelectMany(dir =>
                                matchInternal(parts.Skip(i + 1).ToArray(), dir));
                            return paths;
                        }
                    }
                }

                // if pattern ends in an absolute path with no wildcards in the filename
                var absolute = root + separator + String.Join(separator.ToString(), parts);
                if (File.Exists(absolute))
                    return new[] { absolute };

                return new string[0];
            };

            return matchInternal(split.Skip(1).ToArray(), split[0]);
        }

        public static TempFile GetTempFile(string extension = ".tmp")
        {
            return new TempFile(GetTempFilePath(Path.GetTempPath(), extension));
        }

        internal static string GetTempFilePath(string root, string extension)
        {
            if (!extension.StartsWith("."))
                extension = "." + extension;

            // find unused file name
            string path;
            do
            {
                path = Path.Combine(root, Guid.NewGuid() + extension);
            } while (File.Exists(path));

            return path;
        }

        public static TempFolder GetTempFolder()
        {
            // find unused folder name
            string path;
            do
            {
                path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            } while (Directory.Exists(path));

            return new TempFolder(path);
        }
    }

    public class TempFolder : IDisposable
    {
        public string Path { get; }

        public TempFolder(string path)
        {
            Path = path;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        public string GetTempFilePath(string extension = ".tmp")
        {
            return PathEx.GetTempFilePath(Path, extension);
        }

        public static implicit operator string(TempFolder folder)
        {
            return folder.Path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, true);
                }
                catch
                {
                    /* we don't care about exceptions. */
                }
            }
        }
    }

    public class TempFile : IDisposable
    {
        public string Path { get; }

        public TempFile(string path)
        {
            Path = path;
        }

        public static implicit operator string(TempFile file)
        {
            return file.Path;
        }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                try
                {
                    File.Delete(Path);
                }
                catch
                {
                    /* we don't care about exceptions. */
                }
            }
        }
    }
}
