using System.IO;
using System.Reflection;

namespace Clowd.Upload
{
    internal sealed class Resource : EmbeddedResource
    {
        public Stream ImgurIcon => GetStream("imgur.png");

        public Stream AzureIcon => GetStream("azure.png");

        public Stream MimeDb => GetStream("mime-db.json");

        public Stream LanguageDb => GetStream("languages.yml");

        public Resource() : base(Assembly.GetExecutingAssembly(), "Clowd.Upload.Embed")
        {
        }
    }
}
