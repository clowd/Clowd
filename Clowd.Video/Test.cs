using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Video
{
    internal static class Test
    {
        public static int Main(string[] args)
        {
            // to be used for video testing

            IVideoCapturer ffmpegcap = new FFmpegCapturer();

            var path = ffmpegcap.StartAsync(new System.Drawing.Rectangle(0, 0, 3440, 1440), new VideoCapturerSettings
            {
                OutputDirectory = @"C:\Users\Caelan\Videos"
            }).GetAwaiter().GetResult();

            Console.WriteLine("started.. " + path);

            Thread.Sleep(5000);

            ffmpegcap.StopAsync().GetAwaiter().GetResult();
            Console.WriteLine("stopped");
            Console.ReadLine();
            return 0;
        }
    }
}
