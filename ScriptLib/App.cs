using System;
using System.Threading.Tasks;
using Bridge;
using Bridge.Html5;
using Bridge.jQuery2;

namespace ScriptLib
{
    public class App
    {
        [Ready]
        public static async void Main()
        {
            var options = GetOptions();
            var contentDiv = Document.GetElementById<DivElement>((string) options.contentId);
            

            var compressed = await DownloadPartial("http://caesa.ca/ziptest.zip", "test.txt", 253, 15);
            var decompressed = Inflate(compressed, 0, (int)compressed.ByteLength);
            
            var blob = new Blob(new BlobDataObject[] { decompressed }, new BlobPropertyBag() { Type = "application/octet-stream" });
            blob.SaveAs("hello3.txt");
        }

        [Script("return clowdOptions;")]
        public static dynamic GetOptions()  { return null;  }


        public static ArrayBuffer Inflate(ArrayBuffer input, int offset, int length)
        {
            return Script.Call<ArrayBuffer>("jszlib_inflate_buffer", input, offset, length, null);
        }

        public static Task<ArrayBuffer> DownloadPartial(string url, string fileName, int offset, int length)
        {
            return Task.FromPromise<ArrayBuffer>(
                new Downloader(url, XMLHttpRequestResponseType.ArrayBuffer, offset, length),
                (Func<XMLHttpRequest, ArrayBuffer>)((request) => (ArrayBuffer)request.Response),
                (Action<string>)((status) => Window.Alert(status)));
        }
    }
}