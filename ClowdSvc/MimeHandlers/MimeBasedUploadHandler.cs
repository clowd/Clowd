using Clowd.Shared;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickShareServer
{
    public class MimeBasedUploadHandler
    {
        private static List<IMimeTypeHandler> _handlers;
        static MimeBasedUploadHandler()
        {
            //listed in order of importance.
            _handlers = new List<IMimeTypeHandler>();
            _handlers.Add(new MimeImageHandler());
            _handlers.Add(new MimeTextHandler());
            _handlers.Add(new MimeFileHandler());
        }

        public static RazorUploadTemplate GenerateTemplateForMime(string mimeType, string displayName, CloudBlockBlob blob, RazorUploadTemplate template = null)
        {
            foreach (var hnd in _handlers)
            {
                if (hnd.CheckDoesHandleMime(mimeType))
                {
                    if (template == null)
                        template = new RazorUploadTemplate();
                    hnd.PopulateRazorTemplate(mimeType, displayName, template, blob);
                    return template;
                }
            }
            //no handlers, so return null. although this should never happen since file handler handles all
            return null;
        }
        public static RazorUploadTemplate GenerateTemplateForUploadingFile(string displayName, string downloadUrl, long size, RazorUploadTemplate template = null)
        {
            if (template == null)
                template = new RazorUploadTemplate();
            string extention = Path.GetExtension(displayName).Trim('.').ToUpper();
            template.Type = "Upload in progress";
            //http://www.freeformatter.com/java-dotnet-escape.html
            string downloadHtml = String.Format("<div style=\"height: 100px; max-width:300px; margin:100px auto; text-align:center;\">\r\n<span class=\"text-muted\">{0}</span>\r\n<a href=\"{1}\" style=\"text-decoration: none;\">\r\n<div class=\"button\" style=\"height:50px;\">\r\n<div class=\"button-fill\" style=\"float:left; height:100%; width:50px;\">\r\n<span style=\"line-height:50px;\" class=\"fa fa-2x fa-download\"></span>\r\n</div>\r\n<span style=\"display:block; margin:7px 40px 0 0;\">\r\n<span style=\"font-weight: 600;\">Download File</span>\r\n<br />\r\n<span style=\"font-size:13px;\">{2}</span>\r\n</span>\r\n</div>\r\n</a>\r\n</div>"
                , displayName.ToLower(), downloadUrl, extention + " " + size.ToPrettySizeString());
            template.HtmlContent = downloadHtml;
            return template;
        }
        public static void FinalizeUploadMetadata(string mimeType, string displayName, byte[] data, CloudBlockBlob blob, bool runAsync = true)
        {
            Action handle = new Action(() =>
            {
                foreach (var hnd in _handlers)
                {
                    if (hnd.CheckDoesHandleMime(mimeType))
                    {
                        hnd.FinalizeUploadMetadata(mimeType, displayName, data, blob);
                        break;
                    }
                }
            });
            if (runAsync)
                Task.Factory.StartNew(handle);
            else handle();
        }
    }
}
