//using Mindscape.LightSpeed;
//using RT.Servers;
//using RT.Util.ExtensionMethods;
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Net;
//using System.Net.Sockets;
//using System.Text;
//using System.Threading;
//using System.Threading.Tasks;

//namespace QuickShareServer
//{
//    public class QuickShareServer
//    {
//        public const string FileSaveDirectory = "Files";
//        const int httpPort = 12999;
//        const int tcpPort = 12998;
//        const string contentHeader = "CONTENT-LENGTH";
//        IPAddress listenIP = IPAddress.Parse("127.0.0.1");

//        LightSpeedContext<QuickShareModelUnitOfWork> _dbContext;
//        HttpServer _httpServer;
//        TcpListener _tcpServer;
//        CancellationTokenSource _cancellationToken;
//        Dictionary<string, UploadClient> _tcpClients;

//        public QuickShareServer()
//        {
//            _dbContext = new LightSpeedContext<QuickShareModelUnitOfWork>();
//            _dbContext.ConnectionString = "Server=tcp:tmot89649z.database.windows.net,1433;Database=QuickShareDatabase;User ID=caesay@tmot89649z;Password=He9pat4a;Trusted_Connection=False;Encrypt=True;Connection Timeout=30;";
//            _dbContext.DataProvider = DataProvider.SqlServer2005;

//            _tcpServer = new TcpListener(listenIP, tcpPort);

//            HttpServerOptions hso = new HttpServerOptions();
//            hso.BindAddress = listenIP.ToString();
//            hso.Port = httpPort;
//            _httpServer = new HttpServer(hso);
//            _httpServer.PropagateExceptions = true;
//            _httpServer.Handler = HandleHttpRequest;

//            _cancellationToken = new CancellationTokenSource();

//            _tcpClients = new Dictionary<string, UploadClient>();
//        }

//        public void Start(bool blocking = false)
//        {
//            if (!Directory.Exists(FileSaveDirectory))
//            {
//                Directory.CreateDirectory(FileSaveDirectory);
//            }

//            _httpServer.StartListening(false);
//            _tcpServer.Start();

//            var tcpTask = ListenForTcpClients(_tcpServer, _cancellationToken.Token);

//            if (blocking)
//                tcpTask.Wait();
//        }

//        private async Task ListenForTcpClients(TcpListener server, CancellationToken token)
//        {
//            while (!token.IsCancellationRequested)
//            {
//                var task = server.AcceptTcpClientAsync();
//                try
//                {
//                    var client = await task.WithCancellation(token);
//                    ReadLoopTcpClient(client, token);
//                }
//                catch (OperationCanceledException)
//                {
//                    //task was inturupted by a cancellation token.
//                }
//            }
//        }
//        private async Task ReadLoopTcpClient(TcpClient client, CancellationToken token)
//        {
//            using (client)
//            {
//                string clientKey = Random.GetCryptoUniqueKey(16);
//                var stream = client.GetStream();
//                while (!token.IsCancellationRequested)
//                {                                                             
//                    string command = (await Extensions.ToTask(() => stream.ReadUntil((byte)'\n', false), token)).FromUtf8();
//                    if (String.IsNullOrWhiteSpace(command)) break;
//                    var tmpDict = new SortedDictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
//                    string tmpHeader;
//                    while (!String.IsNullOrWhiteSpace(tmpHeader = (await Extensions.ToTask(() => stream.ReadUntil((byte)'\n', false), token)).FromUtf8()))
//                    {
//                        var split = tmpHeader.Split(':');
//                        tmpDict.Add(split[0].Trim(), split[1].Trim());
//                    }
//                    byte[] payload;
//                    if (tmpDict.ContainsKey(contentHeader))
//                    {
//                        payload = await stream.ReadAsync(Convert.ToInt32(tmpDict[contentHeader]), token);
//                    }
//                    else
//                    {
//                        payload = new byte[0];
//                    }
//                    Packet p = new Packet(command, tmpDict, payload);
//                    Extensions.ToTask(() => HandleTcpPacket(clientKey, client, p));
//                }
//            }
//        }

//        private void HandleTcpPacket(string clientKey, TcpClient tcp, Packet req)
//        {
//            UploadClient client;
//            if (_tcpClients.ContainsKey(clientKey))
//            {
//                client = _tcpClients[clientKey];
//            }
//            else
//            {
//                client = new UploadClient(clientKey, tcp, _dbContext);
//                _tcpClients.Add(clientKey, client);
//            }
//            client.PacketRecieved(req);
//        }
//        private HttpResponse HandleHttpRequest(HttpRequest req)
//        {
//            return null;
//            //using (var uow = _dbContext.CreateUnitOfWork())
//            //{
//            //    if (req.Url.Path.EqualsNoCase("list"))
//            //    {
//            //        StringBuilder sb = new StringBuilder();
//            //        foreach(var entity in uow.UploadEntities)
//            //        {

//            //        }
//            //    }
//            //    else
//            //    {
//            //        Console.WriteLine("path: " + req.Url.Path);
//            //        var get = uow.UploadEntities.FirstOrDefault(up => up.DisplayKey == req.Url.Path);
//            //        if (get == null)
//            //        {
//            //            return HttpResponse.PlainText("no file found");
//            //        }

//            //        if (get is TextUploadEntity)
//            //        {
//            //            return HttpResponse.PlainText((get as TextUploadEntity).Data);
//            //        }
//            //        else if (get is ImageUploadEntity)
//            //        {
//            //            var img = (get as ImageUploadEntity);
//            //            return HttpResponse.File(img.FileName, img.MimeType);
//            //        }
//            //        else
//            //        {
//            //            return HttpResponse.PlainText("can not display data");
//            //        }
//            //    }
//            //}
//        }
//    }
//}
