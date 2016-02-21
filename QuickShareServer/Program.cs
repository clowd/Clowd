using System.Runtime.Caching;
using System.Threading;
using Mindscape.LightSpeed;
using RT.Servers;
using RT.Util;
using RT.Util.ExtensionMethods;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Clowd.Shared;
using CS.Util;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace QuickShareServer
{
    public class Program
    {
        public static MulticastLogger Log = new MulticastLogger();

        public const string ThumbCacheDirectory = "thumb_cache";
        public const string AzureUploadBlobContainerName = "uploads";
        public static CloudBlobContainer AzureUploadBlobContainer;
        public const string AzureBlobEndpoint = "storage.clowd.ca";
        public const int HttpPort = 12999;
        private const int TcpPort = 12998;
        private const string TemplatePath = "template.html";
        public static readonly string ExternalHost = Debugger.IsAttached ? "localhost:12999" : "clowd.ca";
        private static readonly IPAddress ListenIP = IPAddress.Parse("127.0.0.1");

        private static LightSpeedContext<QuickShareModelUnitOfWork> _dbContext;
        private static HttpServer _httpServer;
        private static TcpListener _tcpServer;
        private static CancellationTokenSource _cancellationToken;
        private static string _templateHtml;
        private static ObjectCache _cache;
        private static CacheItemPolicy _directFilePolicy { get { return new CacheItemPolicy { AbsoluteExpiration = DateTimeOffset.Now.AddHours(1) }; } }

        private static void Main(string[] args)
        {
            string storageString = "DefaultEndpointsProtocol=https;AccountName=clowd;AccountKey=dP+QSsPyxvkgFVKRH643mjfV3y9kWMzEKclQqV9/nlOz0u3hJtvmrkAvsIw0vqpSOeWOBpdbk89KrLUUa48URg==";
            string databaseString = "Server=tcp:tmot89649z.database.windows.net,1433;Database=QuickShareDatabase;User ID=caesay@tmot89649z;Password=He9pat4a;Trusted_Connection=False;Encrypt=True;Connection Timeout=30;";

            var conlog = new ConsoleInputLogger();
            conlog.InputRecieved += ConsoleMessageRecieved;
            conlog.VerbosityLimit[LogType.Info] = 2;
            Log.Loggers.Add("console", conlog);
            //TODO: Add file logger

            _dbContext = new LightSpeedContext<QuickShareModelUnitOfWork>();
            _dbContext.ConnectionString = databaseString;
            _dbContext.DataProvider = DataProvider.SqlServer2005;
            _dbContext.IdentityBlockSize = 10;
            //fe2eef39f6234e1b8dffc07571f4d533, hewdailyink @gmail.com, hewdaily
            //rstarkov, ws.clowd@roman.st, 7675cde698b7404c839cd5ac3fae6cbd
            //d346c5fafafc8a5baa56471c122215e5, timwi, clowd @timwi.de

            //var uow = _dbContext.CreateUnitOfWork();
            //User u = new User();
            //string clientsalt = "29AcyQyeqJsQJLCt";
            //u.Username = "timwi";
            //u.Salt = RandomEx.GetCryptoUniqueString(12);
            //var asd = "d346c5fafafc8a5baa56471c122215e5".ToUpper();
            ////var asd2 = "budop95".ComputeMD5(clientsalt);
            //u.Password = Clowd.Shared.MD5.Compute(asd, u.Salt);
            //u.Email = "clowd@timwi.de";
            //u.Subscription = ModelUserDefinedTypes.SubscriptionType.Free;
            //u.SubscriptionPeriod = DateTime.Now.AddMonths(1);
            //u.CreatedDate = DateTime.Now;
            //uow.Add(u);
            //uow.SaveChanges();

            _cache = MemoryCache.Default;

            _tcpServer = new TcpListener(IPAddress.Any, TcpPort);

            HttpServerOptions hso = new HttpServerOptions();
            hso.AddEndpoint("default", ListenIP.ToString(), HttpPort);
            _httpServer = new HttpServer(hso);
            _httpServer.PropagateExceptions = true;

            UrlMapping uploadHook = new UrlMapping(HandleUploadRequest, null, HttpPort, "/u", false, false, false);
            UrlMapping directHook = new UrlMapping(HandleDirectRequest, null, HttpPort, "/d", false, false, false);
            UrlMapping progressHook = new UrlMapping(HandleProgressRequest, null, HttpPort, "/c", false, false, false);
            UrlResolver resolver = new UrlResolver(uploadHook, directHook, progressHook);
            _httpServer.Handler = resolver.Handle;

            _cancellationToken = new CancellationTokenSource();
            if (!Directory.Exists(ThumbCacheDirectory))
                Directory.CreateDirectory(ThumbCacheDirectory);

            CloudStorageAccount account = CloudStorageAccount.Parse(storageString);
            CloudBlobClient client = account.CreateCloudBlobClient();

            //var blobServiceProperties = new Microsoft.WindowsAzure.Storage.Shared.Protocol.ServiceProperties()
            //{
            //    HourMetrics = null,
            //    MinuteMetrics = null,
            //    Logging = null,
            //};
            //blobServiceProperties.Cors.CorsRules.Add(new Microsoft.WindowsAzure.Storage.Shared.Protocol.CorsRule()
            //{
            //    AllowedHeaders = new List<string>() { "*" },
            //    ExposedHeaders = new List<string>() { "*" },
            //    AllowedMethods = Microsoft.WindowsAzure.Storage.Shared.Protocol.CorsHttpMethods.Get,
            //    AllowedOrigins = new List<string>() { "*" },
            //    MaxAgeInSeconds = 3600
            //});
            //client.SetServiceProperties(blobServiceProperties);
            CloudBlobContainer container = client.GetContainerReference(AzureUploadBlobContainerName);
            container.CreateIfNotExists(BlobContainerPublicAccessType.Off);
            Log.Info("Connected to Azure storage. current storage endpoint: " + AzureBlobEndpoint);
            AzureUploadBlobContainer = container;

            _templateHtml = File.ReadAllText(TemplatePath);
            RazorEngine.Razor.Compile(_templateHtml, typeof(RazorUploadTemplate), "templateHtml");
            Log.Info("Razor template compiled successfully");

            if (_httpServer.PropagateExceptions)
                Log.Warn("Http server PropagateExceptions = true");

            _tcpServer.Start();
            var tcpTask = ListenForTcpClients(_tcpServer, _cancellationToken.Token);
            Log.Info("Tcp listening on port " + TcpPort);
            _httpServer.StartListening(false);
            Log.Info("Http listening on port " + HttpPort);
            Log.Info("Ready");
        }

        private static void ConsoleMessageRecieved(object sender, ConsoleInputLoggerEventArgs e)
        {
            if (e.Input.EqualsNoCase("exit") || e.Input.EqualsNoCase("close"))
            {
                var t = new Thread(new ThreadStart(() =>
                {
                    Log.Warn("Starting Shutdown");
                    _cancellationToken.Cancel();
                    _httpServer.StopListening();
                    _tcpServer.Stop();

                    (Log.Loggers["console"] as ConsoleInputLogger).Dispose();
                    Console.WriteLine();
                    Console.WriteLine("Press any key...");
                    Console.ReadKey();
                }));
                t.IsBackground = false;
                t.Start();
            }
            else
                Log.Error("Unrecognized console command: " + e.Input);
        }

        private static HttpResponse HandleProgressRequest(HttpRequest req)
        {
            string key = req.Url.Path.Trim('/');
            long id;
            try { id = key.FromArbitraryBase(36); }
            catch { return HttpResponse.Create("400 Bad Request", "text/plain", RT.Servers.HttpStatusCode._400_BadRequest); }

            Client.UploadContext progress;
            if ((progress = Client.GetInProgressUploadStream(id)) == null)
            {
                using (var uow = _dbContext.CreateUnitOfWork())
                {
                    var upload = uow.Uploads.SingleOrDefault(up => up.Id == key.FromArbitraryBase(36));
                    if (upload == default(Upload))
                        return HttpResponse.Create("404 Not Found", "text/plain", RT.Servers.HttpStatusCode._404_NotFound);
                    if (upload.UploadDate > DateTime.Now.AddMinutes(10))
                        return HttpResponse.Redirect(new HttpUrl(ExternalHost, "/u/" + key));
                    upload.Views = upload.Views + 1;
                    uow.SaveChanges();
                    var blob = AzureUploadBlobContainer.GetBlockBlobReference(upload.StorageKey);
                    var accessPolicy = new SharedAccessBlobPolicy()
                    {
                        Permissions = SharedAccessBlobPermissions.Read,
                        SharedAccessExpiryTime = DateTime.UtcNow.AddMinutes(10),
                        SharedAccessStartTime = DateTime.UtcNow.AddMinutes(-5)
                    };
                    string sasBlobToken = blob.GetSharedAccessSignature(accessPolicy);
                    var direct = "http://" + AzureBlobEndpoint + blob.Uri.AbsolutePath + sasBlobToken;
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
        private static HttpResponse HandleDirectRequest(HttpRequest req)
        {
            return HttpResponse.Create("501 Not Implemented", "text/plain", RT.Servers.HttpStatusCode._501_NotImplemented);
        }

        private static HttpResponse HandleUploadRequest(HttpRequest req)
        {
            string key = req.Url.Path.Trim('/');
            long id;
            try { id = key.FromArbitraryBase(36); }
            catch { return HttpResponse.Create("400 Bad Request", "text/plain", RT.Servers.HttpStatusCode._400_BadRequest); }

            Upload upload = null;
            Client.UploadContext progress;
            if ((progress = Client.GetInProgressUploadStream(id)) != null)
            {
                upload = progress.DbObject;
            }

            RazorUploadTemplate template = new RazorUploadTemplate();
            using (var uow = _dbContext.CreateUnitOfWork())
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
                template = MimeBasedUploadHandler.GenerateTemplateForMime(upload.MimeType, upload.DisplayName, AzureUploadBlobContainer.GetBlockBlobReference(upload.StorageKey), template);
            else
                template = MimeBasedUploadHandler.GenerateTemplateForUploadingFile(progress.DbObject.DisplayName, $"http://{ExternalHost}/c/{key}", progress.FileSize, template);

            if (template == null)
                return HttpResponse.Create("501 Not Implemented", "text/plain", RT.Servers.HttpStatusCode._501_NotImplemented);


            template.WindowTitle = upload.DisplayName;
            template.Views = upload.Views.ToString("N0");
            template.TimePassed = PrettyTime.Format(upload.UploadDate);

            var resp = RazorEngine.Razor.Parse(_templateHtml, template, "templateHtml");
            return HttpResponse.Html(resp);
        }

        private static async Task ListenForTcpClients(TcpListener server, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var task = server.AcceptTcpClientAsync();
                try
                {
                    var client = await task.WithCancellation(token);
                    Client c = new Client(client, token, _dbContext);
                    //TODO: Do something with this?
                }
                catch (OperationCanceledException)
                {
                    //task was inturupted by a cancellation token.
                }
            }
        }
    }
}
