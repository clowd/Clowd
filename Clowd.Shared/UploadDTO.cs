using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Shared
{
    public class UploadDTO
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string Url { get; set; }
        public string PreviewImgUrl { get; set; }
        public string Password { get; set; } = null;
        public DateTime UploadDate { get; set; }
        public DateTime? ValidUntil { get; set; } = null;
        public int? MaxViews { get; set; } = null;
        public int Views { get; set; }
        public bool Hidden { get; set; }
    }
}
