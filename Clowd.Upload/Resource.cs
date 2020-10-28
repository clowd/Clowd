using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Upload
{
    internal sealed class Resource : EmbeddedResource
    {
        public Stream ImgurIcon => GetStream("imgur.png");
        public Stream AzureIcon => GetStream("azure.png");
        public Resource() : base(Assembly.GetExecutingAssembly(), "Clowd.Upload.Images")
        {
        }
    }
}
