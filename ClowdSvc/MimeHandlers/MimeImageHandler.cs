using Clowd.Shared;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickShareServer
{
    public class MimeImageHandler : IMimeTypeHandler
    {
        public const int MaxImageWidth = 950;
        public bool CheckDoesHandleMime(string mimeType)
        {
            return (mimeType == "image/png" || mimeType == "image/jpeg" || mimeType == "image/gif" || mimeType == "image/bmp");
        }

        public void FinalizeUploadMetadata(string mimeType, string displayName, byte[] data, CloudBlockBlob blob)
        {
            using (var ms = new MemoryStream(data))
            using (var img = System.Drawing.Image.FromStream(ms))
            {
                blob.Metadata["dimensions"] = img.Size.Width + "x" + img.Size.Height;
                using (var tmpStream = new MemoryStream())
                using (var thumb = img.GetThumbnailImage(150, 150, () => false, IntPtr.Zero))
                {
                    thumb.Save(tmpStream, System.Drawing.Imaging.ImageFormat.Jpeg);
                    var prevBlob = Program.AzureUploadBlobContainer.GetBlockBlobReference(blob.Name + "/thumb");
                    tmpStream.Position = 0;
                    prevBlob.UploadFromStream(tmpStream);
                    prevBlob.Properties.ContentType = "image/jpeg";
                    prevBlob.SetProperties();
                    blob.Metadata["thumb"] = prevBlob.Uri.AbsolutePath;
                }
                if (img.Size.Width > 1200 || img.Size.Height > 1200)
                {
                    decimal ratio = (decimal)img.Width / img.Height;
                    using (var prevMs = new MemoryStream())
                    using (var preview = CreatePreviewImage(img))
                    {
                        preview.Save(prevMs, System.Drawing.Imaging.ImageFormat.Jpeg);
                        var prevBlob = Program.AzureUploadBlobContainer.GetBlockBlobReference(blob.Name + "/preview");
                        prevMs.Position = 0;
                        prevBlob.UploadFromStream(prevMs);
                        prevBlob.Properties.ContentType = "image/jpeg";
                        prevBlob.SetProperties();
                        blob.Metadata["preview"] = prevBlob.Uri.AbsolutePath;
                    }
                }
                blob.Metadata["size"] = data.LongLength.ToString();
                blob.SetMetadata();
            }
        }

        public void PopulateRazorTemplate(string mimeType, string displayName, RazorUploadTemplate template, CloudBlockBlob blob)
        {
            var accessPolicy = new SharedAccessBlobPolicy()
            {
                Permissions = SharedAccessBlobPermissions.Read,
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(4),
                SharedAccessStartTime = DateTime.UtcNow.AddMinutes(-5)
            };
            string sasBlobToken = blob.GetSharedAccessSignature(accessPolicy);
            string directSrc = "http://" + Program.AzureBlobEndpoint + blob.Uri.AbsolutePath + sasBlobToken;

            template.Type = "Image Upload";
            bool fancyBox = false;
            string preview = "";
            blob.FetchAttributes();
            var metaData = blob.Metadata;
            if (metaData.ContainsKey("dimensions"))
                fancyBox = Convert.ToInt32(metaData["dimensions"].Split('x')[0]) > MaxImageWidth; // if width is over max
            if (metaData.ContainsKey("preview"))
            {
                preview = "http://" + Program.AzureBlobEndpoint + metaData["preview"];
                string blobName = String.Join("/", metaData["preview"].TrimStart('/').Split('/').Skip(1));
                var previewBlob = Program.AzureUploadBlobContainer.GetBlobReference(blobName);
                preview = preview + previewBlob.GetSharedAccessSignature(accessPolicy);
            }
            template.DownloadLink = directSrc;
            template.HtmlContent = "<div style=\"text-align: center;\"><img style=\"max-width:100%;\" ";
            if (fancyBox)
            {
                template.HtmlContent += "class=\"fancybox\" ";
                if (!String.IsNullOrWhiteSpace(preview))
                {
                    template.HtmlContent += "data-big=\"" + directSrc + "\" ";
                    directSrc = preview;
                }
            }
            template.HtmlContent += "src=\"" + directSrc + "\"/></div>";
            if (fancyBox)
            {
                template.HtmlAfterHead = "<link href='http://cdnjs.cloudflare.com/ajax/libs/fancybox/1.3.4/jquery.fancybox-1.3.4.css' rel='stylesheet' type='text/css' media='screen' />";
                template.HtmlAfterBody += "<script type='text/javascript' src='http://cdnjs.cloudflare.com/ajax/libs/fancybox/1.3.4/jquery.fancybox-1.3.4.pack.min.js'></script>\n";
                template.HtmlAfterBody +=
@"<script type='text/javascript'>
    $(function ($) {
        var gallery = true;
        var titlePosition = 'inside';
        $('img.fancybox').each(function () {
            var $this = $(this);
            var title = $this.attr('title');
            var src = $this.attr('data-big') || $this.attr('src');
            var a = $('" + "<a href=\"#\" class=\"fancybox\"></a>" + @"').attr('href', src).attr('title', title);
            $this.wrap(a);
        });
        if (gallery)
            $('a.fancybox').attr('rel', 'fancyboxgallery');
        $('a.fancybox').fancybox({
            titlePosition: titlePosition,
            type:
            'image'
        });
    });
</script>" + "\n";
            }
        }

        private Bitmap CreatePreviewImage(Image src)
        {
            var newSize = ResizeKeepAspect(src.Size, MaxImageWidth, MaxImageWidth);
            Bitmap dest = new Bitmap(src, newSize);
            return dest;
        }
        public static Size ResizeKeepAspect(Size CurrentDimensions, int maxWidth, int maxHeight)
        {
            int newHeight = CurrentDimensions.Height;
            int newWidth = CurrentDimensions.Width;
            if (maxWidth > 0 && newWidth > maxWidth) //WidthResize
            {
                Decimal divider = Math.Abs((Decimal)newWidth / (Decimal)maxWidth);
                newWidth = maxWidth;
                newHeight = (int)Math.Round((Decimal)(newHeight / divider));
            }
            if (maxHeight > 0 && newHeight > maxHeight) //HeightResize
            {
                Decimal divider = Math.Abs((Decimal)newHeight / (Decimal)maxHeight);
                newHeight = maxHeight;
                newWidth = (int)Math.Round((Decimal)(newWidth / divider));
            }
            return new Size(newWidth, newHeight);
        }
    }
}
