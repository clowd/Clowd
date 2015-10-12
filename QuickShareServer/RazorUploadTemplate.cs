using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuickShareServer
{
    public class RazorUploadTemplate
    {
        public string Type { get; set; }
        public string TimePassed { get; set; }
        public string Views { get; set; }
        public string DownloadLink { get; set; }
        public string UploaderName { get; set; }
        public string WindowTitle { get; set; }
        public string HtmlContent { get; set; }
        public string HtmlAfterHead { get; set; }
        public string HtmlAfterBody { get; set; }
    }
}
