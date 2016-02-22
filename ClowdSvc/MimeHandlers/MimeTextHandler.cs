using Clowd.Shared;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClowdSvc
{
    public class MimeTextHandler : IMimeTypeHandler
    {
        public bool CheckDoesHandleMime(string mimeType)
        {
            return mimeType.EndsWith("+xml") || mimeType.StartsWith("text/");
        }

        public void FinalizeUploadMetadata(string mimeType, string displayName, byte[] data, CloudBlockBlob blob)
        {
            //todo check if data should be highlighted.
            blob.Metadata["highlight"] = "true";
            blob.Metadata["size"] = data.LongLength.ToString();
            blob.SetMetadata();
        }

        public void PopulateRazorTemplate(string mimeType, string displayName, RazorUploadTemplate template, CloudBlockBlob blob)
        {
            var accessPolicy = new SharedAccessBlobPolicy()
            {
                Permissions = SharedAccessBlobPermissions.Read,
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(1),
                SharedAccessStartTime = DateTime.UtcNow.AddMinutes(-5)
            };
            string sasBlobToken = blob.GetSharedAccessSignature(accessPolicy);

            template.Type = "Text Upload";
            string directSrc = "http://" + Program.AzureBlobEndpoint + blob.Uri.AbsolutePath + sasBlobToken;
            template.HtmlContent = "<pre><code class=\"result\"></code></pre>";
            template.HtmlAfterHead = "<link href='http://cdnjs.cloudflare.com/ajax/libs/highlight.js/8.7/styles/default.min.css' rel='stylesheet' type='text/css'>";
            template.HtmlAfterBody = "<script type='text/javascript' src='http://cdnjs.cloudflare.com/ajax/libs/highlight.js/8.7/highlight.min.js'></script>\n";
            template.HtmlAfterBody += "<script type='text/javascript'> $.get('" + directSrc + "', function(data) { var res = $('.result'); res.text(data); hljs.highlightBlock(res[0]); }); </script>";
            template.DownloadLink = directSrc;
        }
    }
}
