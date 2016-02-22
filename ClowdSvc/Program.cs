using System;
using System.IO;
using System.Reflection;
using Anotar.NLog;
using Topshelf;

namespace Clowd.Server
{
    public class Program
    {
        private static void Main(string[] args)
        {
            var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!Directory.Exists(Path.Combine(baseDir, "logs")))
                Directory.CreateDirectory(Path.Combine(baseDir, "logs"));
            if (!Directory.Exists(Path.Combine(baseDir, "logs", "archive")))
                Directory.CreateDirectory(Path.Combine(baseDir, "logs", "archive"));

            var host = HostFactory.New(x =>
            {
                x.SetServiceName("ClowdSvc");
                x.SetDescription("Clowd Windows Service Host");
                x.Service<ClowdService>();
                x.StartAutomatically();
                x.EnableServiceRecovery(r =>
                {
                    r.RestartService(0);
                    r.RestartService(5);
                    r.RestartService(30);
                    r.SetResetPeriod(1);
                });
                x.RunAsNetworkService();
                x.UseNLog();
            });
            host.Run();
        }
    }
}