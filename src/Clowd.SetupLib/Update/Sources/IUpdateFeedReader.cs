using System.Collections.Generic;
using NAppUpdate.Framework.Tasks;

namespace NAppUpdate.Framework.FeedReaders
{
    public class NauFeed
    {
        public string BaseUrl { get; set; }
        public long TotalSize { get; set; }
        public long CompressedSize { get; set; }
        public string CompressedFilePath { get; set; }
        public IList<IUpdateTask> Tasks { get; set; }
    }

    public interface IUpdateFeedReader
    {
        NauFeed Read(string feed);
    }
}
