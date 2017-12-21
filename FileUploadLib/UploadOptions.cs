using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileUploadLib
{
    public class UploadOptions
    {
        public string FileName { get; set; }

        public bool UseUniqueUploadKey { get; set; } = true;

        /// <summary>
        /// We will subtitute the following variables in this string and then provide it back as the final URL after uploading
        /// #{uk} - Upload Key
        /// #{mt} - Mime Type (calculated from file name)
        /// </summary>
        public string CustomUrlPattern { get; set; }
    }
}
