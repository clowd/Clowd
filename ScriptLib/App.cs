using System;
using System.Threading.Tasks;
using Bridge;
using Bridge.Html5;

namespace ScriptLib
{
    public class App
    {
        public static async void Main2()
        {
            var compressed = await DownloadPartial("http://caesa.ca/ziptest.zip", "test.txt", 253, 15);
            var decompressed = Inflate(compressed, 0, (int)compressed.ByteLength);

            var blob = new Blob(new BlobDataObject[] { decompressed }, new BlobPropertyBag() { Type = "application/octet-stream" });
            blob.SaveAs("hello3.txt");
        }

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

    public class Encoder
    {
        [Script("return String.fromCharCode.apply(null, new Uint8Array(buf));")]
        public static string Utf8ArrayToString(ArrayBuffer buf) { return null; }

        [Script("return String.fromCharCode.apply(null, new Uint16Array(buf));")]
        public static string Utf16ArrayToString(ArrayBuffer buf) { return null; }

        public static ArrayBuffer StringToUtf16Array(string str)
        {
            var buf = new ArrayBuffer(str.Length * 2);
            var bufView = new Uint16Array(buf);
            var strLen = str.Length;
            for (var i = 0; i < strLen; i++)
            {
                bufView[i] = (ushort)str.CharCodeAt(i);
            }
            return buf;
        }
        public static ArrayBuffer StringToUtf8Array(string str)
        {
            var buf = new ArrayBuffer(str.Length);
            var bufView = new Uint8Array(buf);
            var strLen = str.Length;
            for (var i = 0; i < strLen; i++)
            {
                bufView[i] = (byte)str.CharCodeAt(i);
            }
            return buf;
        }

    }
}