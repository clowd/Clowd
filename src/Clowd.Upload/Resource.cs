using System.IO;
using System.Reflection;

namespace Clowd.Upload
{
    internal sealed class Resource : EmbeddedResource
    {
        public Stream ImgurIcon => GetStream("imgur.png");

        public Stream BackBlazeIcon => GetStream("backblaze.png");

        public Stream AzureIcon => GetStream("azure.png");
        
        public Stream VgyMeIcon => GetStream("vgyme.png");

        // https://github.com/jshttp/mime-db/blob/master/db.json
        public Stream MimeDb => GetStream("mime-db.json");

        // https://github.com/github/linguist/blob/master/lib/linguist/languages.yml
        public Stream LanguageDb => GetStream("languages.yml");

        public Resource() : base(Assembly.GetExecutingAssembly(), "Clowd.Upload.Embed")
        { }
    }
}
