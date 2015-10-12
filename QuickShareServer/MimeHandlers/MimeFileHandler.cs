using Clowd.Shared;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RT.Util.ExtensionMethods;

namespace QuickShareServer
{
    public class MimeFileHandler : IMimeTypeHandler
    {
        public bool CheckDoesHandleMime(string mimeType)
        {
            return true;
        }

        public void FinalizeUploadMetadata(string mimeType, string displayName, byte[] data, CloudBlockBlob blob)
        {
            blob.Metadata["size"] = data.LongLength.ToString();
            blob.SetMetadata();
        }

        public void PopulateRazorTemplate(string mimeType, string displayName, RazorUploadTemplate template, CloudBlockBlob blob)
        {
            blob.FetchAttributes();
            string size = "??? B";
            if (blob.Metadata.ContainsKey("size"))
                size = Convert.ToInt64(blob.Metadata["size"]).ToPrettySizeString();
            template.Type = "Generic Upload";

            var accessPolicy = new SharedAccessBlobPolicy()
            {
                Permissions = SharedAccessBlobPermissions.Read,
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(4),
                SharedAccessStartTime = DateTime.UtcNow.AddMinutes(-5)
            };

            string sasBlobToken = blob.GetSharedAccessSignature(accessPolicy, new SharedAccessBlobHeaders()
            {
                ContentDisposition = "attachment; filename=" + displayName
            });

            string directSrc = "http://" + Program.AzureBlobEndpoint + blob.Uri.AbsolutePath + sasBlobToken;
            string extention = Path.GetExtension(displayName).Trim('.').ToUpper();
            //http://www.freeformatter.com/java-dotnet-escape.html
            string downloadHtml = String.Format("<div style=\"height: 100px; max-width:300px; margin:100px auto; text-align:center;\">\r\n<span class=\"text-muted\">{0}</span>\r\n<a href=\"{1}\" style=\"text-decoration: none;\">\r\n<div class=\"button\" style=\"height:50px;\">\r\n<div class=\"button-fill\" style=\"float:left; height:100%; width:50px;\">\r\n<span style=\"line-height:50px;\" class=\"fa fa-2x fa-download\"></span>\r\n</div>\r\n<span style=\"display:block; margin:7px 40px 0 0;\">\r\n<span style=\"font-weight: 600;\">Download File</span>\r\n<br />\r\n<span style=\"font-size:13px;\">{2}</span>\r\n</span>\r\n</div>\r\n</a>\r\n</div>"
                , displayName.ToLower(), directSrc, extention + " " + size);
            template.HtmlContent = downloadHtml;
        }
    }
}
