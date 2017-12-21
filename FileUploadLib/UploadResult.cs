using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileUploadLib
{
    public class UploadResult
    {
        public string Provider { get; set; }
        public string UploadKey { get; set; }
        public DateTimeOffset UploadTime { get; set; }
        public string ContentType { get; set; }
        public string FileName { get; set; }
        public string PublicUrl { get; set; }
    }
}
