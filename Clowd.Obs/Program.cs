using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Obs
{
    class Program
    {
        static void Main(string[] args)
        {
            Run().GetAwaiter().GetResult();
            Console.ReadLine();
        }

        static async Task Run()
        {
            var cap = new ObsCapturer();

            cap.StatusReceived += Cap_StatusReceived;

            await cap.StartAsync(new System.Drawing.Rectangle(0, 0, 3440, 1440), new VideoCapturerSettings
            {
                OutputDirectory = @"C:\Users\Caelan\Videos",
            });

            await Task.Delay(5000);

            await cap.StopAsync();

            await Task.Delay(2000);
            Console.WriteLine("shutting down..");
            cap.Dispose();
        }

        private static void Cap_StatusReceived(object sender, VideoStatusEventArgs e)
        {
            Console.WriteLine($"{e.AvgFps} fps - dropped {e.DroppedFrames}");
        }
    }
}
