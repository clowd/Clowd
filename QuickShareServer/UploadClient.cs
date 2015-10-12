using Mindscape.LightSpeed;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace QuickShareServer
{
    public class UploadClient
    {
        public string ClientKey { get; private set; }
        public string DisplayKey { get; private set; }
        public string AdministerKey { get; private set; }
        public bool AllowDownload { get; private set; }
        public int PercentComplete
        {
            get
            {
                return _currentChunk / _expectedChunks * 100;
            }
        }

        public Action<Packet> PacketRecieved;

        private Upload _dbEntity;
        private TcpClient _tcpClient;
        private MemoryStream _payload;
        private LightSpeedContext<QuickShareModelUnitOfWork> _dbContext;

        private string _contentType;
        private int _expectedChunks;
        private int _currentChunk;
        private string _md5hash;
        private bool _acceptUpload = false;
        private bool _public = true;
        private int _restrictedViews = -1;
        private string _author;
        private string _password = null;
        private string _fileName;
        private DateTime _validUntil;

        public UploadClient(string clientKey, TcpClient client, LightSpeedContext<QuickShareModelUnitOfWork> context)
        {
            ClientKey = clientKey;
            _tcpClient = client;
            _dbContext = context;
            AllowDownload = false;
            InitializeHandlers();
        }

        private void InitializeHandlers()
        {
            var handlers = new List<PacketHandlerIntermediate>();
            DynamicMethodDelegate catchall = null;
            foreach (MethodInfo method in this.GetType().GetMethods())
            {
                foreach (Attribute attr in method.GetCustomAttributes(true))
                {
                    if (attr is PacketHandlerAttribute)
                    {
                        PacketHandlerIntermediate phi = new PacketHandlerIntermediate(attr as PacketHandlerAttribute, method);
                        handlers.Add(phi);
                    }
                    else if (attr is PacketCatchAllAttribute)
                    {
                        catchall = DynamicMethodFactory.Generate(method);
                    }
                }
            }

            PacketRecieved = new Action<Packet>((obj) =>
            {
                string command = obj.Command;
                var hndlrs = handlers.Where(phi => phi.Command.Equals(obj.Command, StringComparison.InvariantCultureIgnoreCase));
                if (hndlrs.Count() == 0 && catchall != null)
                {
                    object[] args = { obj };
                    catchall(this, args);
                }
                else
                {
                    foreach (var hn in hndlrs)
                    {
                        hn.Call(this, obj);
                    }
                }
            });
        }

        [PacketHandler("BEGIN")]
        private void Packet_Begin(Packet p)
        {
            _contentType = p.Headers["content-type"];
            _currentChunk = 0;
            //_md5hash = p.Headers["md5"];
            _author = p.Headers["username"];
            //bool allowDownloadInProgress = p.Headers["allowdlinprogress"] == "true";
            _public = p.Headers["public"] == "true";
            string password = p.Headers["password"];

            AdministerKey = Random.GetCryptoUniqueKey(30);
            DisplayKey = Random.GetCryptoUniqueKey(9);

            if (_contentType == "image")
            {
                _acceptUpload = true;
                var response = new Packet();
                response.Command = "BEGIN";
                response.Headers.Add("Download-Elegible", "false");
                _expectedChunks = Convert.ToInt32(p.Headers["chunks"]);
                _fileName = p.Headers["filename"];
                //_tcpClient.
            }
            else if (_contentType == "text")
            {
                string displayType = p.Headers["display"];
                string data = p.Payload;

                //TextUploadEntity en = new TextUploadEntity();
                //en.AdminKey = AdministerKey;
                //en.AllowedViews = _restrictedViews;
                //en.AuthorUsername = _author;
                //en.ContentType = (int)SharedTypes.UploadType.Image;
                //en.DisplayKey = DisplayKey;
                //en.Password = _password;
                //en.Public = _public;
                //en.UploadDate = DateTime.Now;
                //en.ValidUntil = _validUntil;
                //en.Data = data;
                //en.DisplayMode = displayType;

                //using (var uow = _dbContext.CreateUnitOfWork())
                //{
                //    uow.Add(en);
                //    uow.SaveChanges();
                //}
            }
            else if (_contentType == "file")
            {
                _expectedChunks = Convert.ToInt32(p.Headers["chunks"]);
                _acceptUpload = true;
                _fileName = p.Headers["filename"];
            }
        }

        [PacketHandler("UPLOADCHUNK")]
        private void Packet_Upload(Packet p)
        {
            if (!_acceptUpload || !p.HasPayload)
                return;

            _currentChunk = Convert.ToInt32(p.Headers["chunk"]);
            bool final = p.Headers["last"] == "true";
            _payload.Write(p.PayloadBytes, 0, p.PayloadBytes.Length);

            if (final && _currentChunk == _expectedChunks)
            {
                UploadComplete_Image();
            }
        }

        private void UploadComplete_Image()
        {
            var saveDirectory = QuickShareServer.FileSaveDirectory;
            var ms = _payload;
            using (FileStream file = new FileStream(Path.Combine(saveDirectory, DisplayKey), FileMode.Create, System.IO.FileAccess.Write))
            {
                byte[] bytes = new byte[ms.Length];
                ms.Read(bytes, 0, (int)ms.Length);
                file.Write(bytes, 0, bytes.Length);
                ms.Close();
            }

            //ImageUploadEntity en = new ImageUploadEntity();
            //en.AdminKey = AdministerKey;
            //en.AllowedViews = _restrictedViews;
            //en.AuthorUsername = _author;
            //en.ContentType = (int)SharedTypes.UploadType.Image;
            //en.DisplayKey = DisplayKey;
            //en.FileName = _fileName;
            //en.MimeType = MIMEAssistant.GetMIMEType(_fileName);
            //en.Password = _password;
            //en.Public = _public;
            //en.UploadDate = DateTime.Now;
            //en.ValidUntil = _validUntil;

            //using (var uow = _dbContext.CreateUnitOfWork())
            //{
            //    uow.Add(en);
            //    uow.SaveChanges();
            //}
        }
    }


}
