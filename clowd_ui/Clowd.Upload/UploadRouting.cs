using System;
using System.IO;

namespace Clowd.Upload
{
    /// <summary>The zip-or-direct routing decision for a set of dropped/shared paths.</summary>
    internal sealed class ZipDecision
    {
        /// <summary>True when the paths should be zipped into a single archive before upload.</summary>
        public bool Zip { get; set; }

        /// <summary>Fixed archive name (single dangerous file → "&lt;name&gt;.zip"); null means
        /// the caller picks a random name.</summary>
        public string ArchiveName { get; set; }
    }

    /// <summary>
    /// Pure decision logic behind UploadManager.UploadSeveralFiles, factored out of the UI layer
    /// so it is unit testable. Filesystem access is injected.
    /// </summary>
    internal static class UploadRouting
    {
        public static ZipDecision ShouldZip(string[] filePaths, bool wrapDangerousUploads, IMimeProvider mime,
            Func<string, bool> fileExists, Func<string, long> getFileLength)
        {
            // multiple paths, a directory, or a missing file always route to the archive path
            if (filePaths.Length != 1 || !fileExists(filePaths[0]))
                return new ZipDecision { Zip = true };

            var path = filePaths[0];
            var ext = Path.GetExtension(path);

            // browsers block or warn on direct downloads of executables and similar, so a zip
            // wrapper keeps the shared link usable; the archive keeps the original name so the
            // recipient sees "tool.exe.zip" rather than a random string.
            if (wrapDangerousUploads && DangerousFileTypes.IsDangerous(ext))
                return new ZipDecision { Zip = true, ArchiveName = Path.GetFileName(path) + ".zip" };

            // zip the single file if:
            // - the file type is unknown / is not a special type like image (can not be rendered nicely in browser)
            // - we think the mime type might be compressible
            // - the file size is > 5mb
            var entry = mime.GetMimeFromExtension(ext);
            var category = mime.GetCategoryFromExtension(ext);
            var compress = category == ContentCategory.Unknown && entry.Compressible != false && getFileLength(path) > 1024 * 1024 * 5;

            return new ZipDecision { Zip = compress };
        }
    }
}
