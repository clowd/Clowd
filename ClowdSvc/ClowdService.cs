using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anotar.NLog;
using Clowd.Server.Config;
using Clowd.Server.MimeHandlers;
using Clowd.Server.Util;
using Clowd.Shared;
using Exceptionless;
using Microsoft.WindowsAzure.Storage.Blob;
using Mindscape.LightSpeed;
using RT.Servers;
using RT.Util;
using RT.Util.ExtensionMethods;
using Topshelf;

namespace Clowd.Server
{
    public class ClowdService : ServiceControl
    {
        public AzureStorageClient Storage { get; private set; }
        public ClowdConfigSection Config { get; private set; }
        public LightSpeedContext<DatabaseModelUnitOfWork> Database { get; private set; }
        public CancellationToken CancelToken => _cancellationSource.Token;
        public string TempPath { get; private set; }

        private HttpServer _httpServer;
        private TcpListener _tcpServer;
        private CancellationTokenSource _cancellationSource;
        private string _templateHtml;

        public bool Start(HostControl hostControl)
        {
            var exless = ExceptionlessClient.Default;
            var exconf = exless.Configuration;
            exconf.ApiKey = "0pKKyx80TiGL6IxZvQmvj416gawm2kM0IvZDcqG4";
            exconf.ServerUrl = "https://exless.caesa.ca";
            exconf.UseInMemoryStorage();
            if (Debugger.IsAttached)
                exconf.Enabled = false;
            else
                exless.Register();

            var conf = ConfigurationManager.GetSection("clowdConfig") as ClowdConfigSection;
            if (conf == null)
                throw new ArgumentNullException("config", "clowdConfig must be defined in app.config");
            Config = conf;

            string tmpPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "temp");
            if (!Directory.Exists(tmpPath))
                Directory.CreateDirectory(tmpPath);
            LogTo.Info("Using temp path: " + tmpPath);
            TempPath = tmpPath;

            LogTo.Info($"Updated and loaded {MimeHelper.Init()} mime types.");

            Storage = new AzureStorageClient();
            LogTo.Info("Connected to Azure storage. current storage endpoint: " + Storage.Endpoint);

            Database = new LightSpeedContext<DatabaseModelUnitOfWork>("default");
            int dbcount;
            using (var uow = Database.CreateUnitOfWork())
                dbcount = uow.Uploads.Count();
            LogTo.Info($"Completed database test query: {dbcount} stored uploads.");

            HttpServerOptions hso = new HttpServerOptions();
            hso.AddEndpoint("default", conf.ListenIP, conf.HttpPort);
            //hso.CertificatePath = "clowd.pfx";
            _httpServer = new HttpServer(hso);
            if (Debugger.IsAttached)
                _httpServer.PropagateExceptions = true;

            UrlMapping uploadHook = new UrlMapping(HandleUploadRequest, null, conf.HttpPort, "/u", false, false, false);
            UrlMapping directHook = new UrlMapping(HandleDirectRequest, null, conf.HttpPort, "/d", false, false, false);
            UrlMapping progressHook = new UrlMapping(HandleProgressRequest, null, conf.HttpPort, "/c", false, false, false);
            UrlResolver resolver = new UrlResolver(uploadHook, directHook, progressHook);
            _httpServer.Handler = resolver.Handle;

            _cancellationSource = new CancellationTokenSource();

            _templateHtml = File.ReadAllText("template.html");
            RazorEngine.Razor.Compile(_templateHtml, typeof(RazorUploadTemplate), "templateHtml");
            LogTo.Info("Razor template compiled successfully");

            if (_httpServer.PropagateExceptions)
                LogTo.Warn("Http server PropagateExceptions = true");

            _tcpServer = new TcpListener(IPAddress.Any, conf.ServicePort);
            _tcpServer.Start();
            var task = ListenForTcpClients(_tcpServer, _cancellationSource.Token);
            LogTo.Info("TCP listening on port " + conf.ServicePort);

            _httpServer.StartListening(false);
            LogTo.Info("HTTP listening on port " + conf.HttpPort);
            LogTo.Info("Ready");
            return true;
        }
        public bool Stop(HostControl hostControl)
        {
            LogTo.Warn("Starting Shutdown (requested)");
            _cancellationSource.Cancel();
            _httpServer.StopListening(blocking: true);
            _tcpServer.Stop();
            return true;
        }

        private HttpResponse HandleProgressRequest(HttpRequest req)
        {
            string key = req.Url.Path.Trim('/');
            long id;
            try { id = key.FromArbitraryBase(36); }
            catch { return HttpResponse.Create("400 Bad Request", "text/plain", RT.Servers.HttpStatusCode._400_BadRequest); }

            Client.UploadContext progress;
            if ((progress = Client.GetInProgressUploadStream(id)) == null)
            {
                using (var uow = Database.CreateUnitOfWork())
                {
                    var upload = uow.Uploads.SingleOrDefault(up => up.Id == key.FromArbitraryBase(36));
                    if (upload == default(Upload))
                        return HttpResponse.Create("404 Not Found", "text/plain", RT.Servers.HttpStatusCode._404_NotFound);
                    if (upload.UploadDate > DateTime.Now.AddMinutes(10))
                        return HttpResponse.Redirect(new Uri(Config.ExternalEndpoint).Append("u", key).AbsolutePath);
                    upload.Views = upload.Views + 1;
                    uow.SaveChanges();

                    var blob = Storage.GetBlob(upload.Container, upload.StorageKey);
                    var accessPolicy = new SharedAccessBlobPolicy()
                    {
                        Permissions = SharedAccessBlobPermissions.Read,
                        SharedAccessExpiryTime = DateTime.UtcNow.AddMinutes(10),
                        SharedAccessStartTime = DateTime.UtcNow.AddMinutes(-5)
                    };
                    string sasBlobToken = blob.GetSharedAccessSignature(accessPolicy);
                    var direct = new Uri(Storage.Endpoint).Append(blob.Uri.AbsolutePath + sasBlobToken).AbsolutePath;
                    //var direct = "http://" + AzureBlobEndpoint + blob.Uri.AbsolutePath + sasBlobToken;
                    //var url = new HttpUrl(AzureBlobEndpoint, blob.Uri.AbsolutePath + sasBlobToken);
                    //Program.Log.Info(url.ToString());
                    return HttpResponse.Create("", "", RT.Servers.HttpStatusCode._301_MovedPermanently, new HttpResponseHeaders()
                    {
                        Location = direct
                    });
                }
            }

            progress.DbObject.Views++;
            HttpResponseHeaders hed = new HttpResponseHeaders
            {
                ContentLength = progress.FileSize,
                ContentDisposition =
                    new HttpContentDisposition()
                    {
                        Filename = progress.DbObject.DisplayName,
                        Mode = HttpContentDispositionMode.Attachment
                    }
            };
            var hr = HttpResponse.Create(progress.PayloadStream.CloneStream(), "application/octet-stream", headers: hed);
            hr.UseGzip = UseGzipOption.DontUseGzip;
            return hr;
        }
        private HttpResponse HandleDirectRequest(HttpRequest req)
        {
            return HttpResponse.Create("501 Not Implemented", "text/plain", RT.Servers.HttpStatusCode._501_NotImplemented);
        }
        private HttpResponse HandleUploadRequest(HttpRequest req)
        {
            string key = req.Url.Path.Trim('/');
            long id;
            try { id = key.FromArbitraryBase(36); }
            catch { return HttpResponse.Create("400 Bad Request", "text/plain", RT.Servers.HttpStatusCode._400_BadRequest); }

            Upload upload = null;
            Client.UploadContext progress = null;
            if ((progress = Client.GetInProgressUploadStream(id)) != null)
            {
                upload = progress.DbObject;
            }

            RazorUploadTemplate template = new RazorUploadTemplate();
            using (var uow = Database.CreateUnitOfWork())
            {
                if (upload == null)
                {
                    upload = uow.Uploads.SingleOrDefault(up => up.Id == key.FromArbitraryBase(36));
                    if (upload == default(Upload))
                        return HttpResponse.Create("404 Not Found", "text/plain", RT.Servers.HttpStatusCode._404_NotFound);
                }
                upload.Views = upload.Views + 1;
                uow.SaveChanges();
                if (upload.Owner != null)
                    template.UploaderName = upload.Owner.Username;
            }

            if (progress == null)
                template = MimeBasedUploadHandler.GenerateTemplateForMime(upload.MimeType,
                    upload.DisplayName, Storage.GetBlob(upload.Container, upload.StorageKey), template);
            else
                template = MimeBasedUploadHandler.GenerateTemplateForUploadingFile(progress.DbObject.DisplayName,
                    $"http://{Config.ExternalEndpoint}/c/{key}", progress.FileSize, template);

            if (template == null)
                return HttpResponse.Create("501 Not Implemented", "text/plain", RT.Servers.HttpStatusCode._501_NotImplemented);


            template.WindowTitle = upload.DisplayName;
            template.Views = upload.Views.ToString("N0");

            template.TimePassed = CS.Util.PrettyTime.Format(upload.UploadDate);
            var resp = RazorEngine.Razor.Parse(_templateHtml, template, "templateHtml");
            return HttpResponse.Html(resp);
        }

        private async Task ListenForTcpClients(TcpListener server, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var task = server.AcceptTcpClientAsync();
                try
                {
                    var client = await task.WithCancellation(token);
                    Client c = new Client(client, new ServiceContext(this));
                    //TODO: Do something with this?
                }
                catch (OperationCanceledException)
                {
                    //task was inturupted by a cancellation token.
                }
            }
        }
    }
    public class ServiceContext
    {
        public AzureStorageClient Storage => _instance.Storage;
        public ClowdConfigSection Config => _instance.Config;
        public LightSpeedContext<DatabaseModelUnitOfWork> Database => _instance.Database;
        public CancellationToken Token => _instance.CancelToken;
        public string TempPath => _instance.TempPath;

        private readonly ClowdService _instance;
        internal ServiceContext(ClowdService instance)
        {
            _instance = instance;
        }
    }
}
