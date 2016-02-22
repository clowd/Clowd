using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mindscape.LightSpeed;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;
using System.Runtime.Caching;
using Anotar.NLog;
using Clowd.Server.Util;
using Clowd.Shared;
using CS.Util;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Clowd.Server
{
    public class Client
    {
        private TcpClient _tcp;
        private CancellationToken _token;
        private const string contentHeader = "CONTENT-LENGTH";
        private LightSpeedContext<DatabaseModelUnitOfWork> _dbContext;
        private DatabaseModelUnitOfWork _uow;
        private bool _validConnection = false;
        private string _nextPacket = null;
        private string _remoteIP = null;
        private UploadContext _context;
        private User _user;
        private string _tempAuthStr = null;
        private User _tempAuthUsr = null;
        private ServiceContext _svcContext;

        private static List<UploadContext> _uploadsInProgress = new List<UploadContext>();
        private static MemoryCache _sessionCache = new MemoryCache("sessionCache");

        public Client(TcpClient tcp, ServiceContext context)
        {
            _tcp = tcp;
            _svcContext = context;
            _token = context.Token;
            _dbContext = context.Database;
            _remoteIP = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address.ToString();
            _validConnection = true;
            LogTo.Info(_remoteIP + ": Client Connected");

            var handlePacket = PacketHandlerFactory.InitializeHandlerForObject(this);
            Action<Packet> inm = (p) =>
            {
                try
                {
                    if (!_validConnection) return;
                    if (String.IsNullOrEmpty(_nextPacket))
                        handlePacket(p);
                    else if (_nextPacket.EqualsNoCase(p.Command))
                    {
                        handlePacket(p);
                    }
                    else //got packet wasnt expecting
                    {
                        WriteErrorAndClose("Expecting Packet " + _nextPacket);
                    }
                }
                catch (Exception e)
                {
                    WriteErrorAndClose(e.Message);
                }
            };
            var readTask = ReadLoopTcpClient(_tcp, _token, inm);
            _uow = _dbContext.CreateUnitOfWork();
            readTask.ContinueWith(read =>
            {
                //by reading this, (if it exists) its marked as handled and wont throw when it gets garbage collected
                var ex = read.Exception;
                _tcp.Close();
                _uow.Dispose();
                LogTo.Info(_remoteIP + ": Client Disconnected");
            });
        }

        public static UploadContext GetInProgressUploadStream(long key)
        {
            return _uploadsInProgress.SingleOrDefault(uplo => uplo.DbObject.Id == key);
        }

        #region Auth packets
        [PacketHandlerFactory.PacketHandler("SESSIONCK")]
        internal void SESSIONCK(Packet p)
        {
            //INC PACKET [SESSIONCK]
            //Payload: [session key]
            //OUT PACKET [SESSIONCKRESP]
            //valid: [true/false]
            string valid = "false";
            var obj = _sessionCache.Get(p.Payload);
            if (obj != null && obj is long)
            {
                valid = "true";

                var getuser = _uow.FindById<User>((long)obj);
                if (_user != null && _user != getuser)
                    throw new InvalidOperationException("User tried to authenticate as two different users in the same session.");

                LogTo.Info(_remoteIP + $": Authenticated as: {getuser.Username}");
                _user = getuser;
            }
            var pr = new Packet { Command = "SESSIONCKRESP" };
            pr.Headers.Add("valid", valid);
            Write(pr);
        }
        [PacketHandlerFactory.PacketHandler("LOGIN")]
        internal void LOGIN(Packet p)
        {
            //INC PACKET [LOGIN]
            //Payload: [username]
            //Out PACKET [LOGINRESP]
            //iv: [_tempAuthStr]
            //salt: [user.Salt]

            Packet resp = new Packet() { Command = "LOGINRESP" };

            var usr = _uow.Users.SingleOrDefault(u => u.Username.ToUpper() == p.Payload.ToUpper());
            if (usr == null)
            {
                LogTo.Info(_remoteIP + $": Failed LOGIN: {p.Payload}, no such user");
                resp.Headers.Add("error", "no user");
                Write(resp);
                return;
            }

            _tempAuthStr = RandomEx.GetCryptoUniqueString(12);
            _tempAuthUsr = usr;
            resp.Headers.Add("iv", _tempAuthStr);
            resp.Headers.Add("salt", usr.Salt);
            Write(resp);

            _nextPacket = "AUTH";
        }
        [PacketHandlerFactory.PacketHandler("AUTH")]
        internal void AUTH(Packet p)
        {
            //INC PACKET [AUTH]
            //Payload: [passwordHash]
            //Out PACKET [AUTHRESP]
            //Payload: [sessionKey]
            if (_tempAuthUsr == null || _tempAuthStr == null)
                throw new InvalidOperationException("A call to AUTH must be preceded by a call to LOGIN");
            Packet resp = new Packet() { Command = "AUTHRESP" };
            if (MD5.Compute(_tempAuthUsr.Password, _tempAuthStr) == p.Payload)
            {
                _user = _tempAuthUsr;
                var session = RandomEx.GetCryptoUniqueString(24);
                CacheItemPolicy cip = new CacheItemPolicy();
                cip.SlidingExpiration = TimeSpan.FromHours(6);
                _sessionCache.Add(session, _user.Id, cip);
                resp.Payload = session;
                resp.Headers["username"] = _user.Username;
                resp.Headers["email"] = _user.Email;
                resp.Headers["subscription"] = ((int)_user.Subscription).ToString();
                resp.Headers["uploads"] = _user.Uploads.Count().ToString();
                Write(resp);
                LogTo.Info(_remoteIP + $": Logged in as: {_tempAuthUsr.Username}");
            }
            else
            {
                LogTo.Info(_remoteIP + $": Failed AUTH: {_tempAuthUsr.Username}, incorrect password");
                resp.Headers.Add("error", "invalid password");
                Write(resp);
            }
            _tempAuthStr = null;
            _tempAuthUsr = null;
            _nextPacket = null;
        }
        #endregion

        #region User packets

        [PacketHandlerFactory.PacketHandler("MYUPLOADS")]
        internal void MYUPLOADS(Packet p)
        {
            //INC PACKET [MYUPLOADS]
            //offset: [offset to return uploads from]
            //count: [num of uploads to return] (max 50, default 10)
            //OUT PACKET [UPLOADS]
            //count [num of uploads actually returned]
            //Payload [json list of UploadDTO's]

            if (_user == null)
                throw new InvalidOperationException("Must be authenticated to call MYUPLOADS");
            int offset = 0;
            if (p.Headers.ContainsKey("offset"))
                offset = ExactConvert.To<int>(p.Headers["offset"]);
            int count = 10;
            if (p.Headers.ContainsKey("count"))
                count = Math.Min(ExactConvert.To<int>(p.Headers["count"]), 50);


            var uploads = from up in _user.Uploads.OrderByDescending(up => up.UploadDate).Skip(offset).Take(count)
                          let displaykey = up.Id.ToArbitraryBase(36)
                          select new UploadDTO()
                          {
                              Key = displaykey,
                              Url = $"{_svcContext.Config.ExternalEndpoint}/u/{displaykey}",
                              //PreviewImgUrl = $"http://{Program.ExternalHost}/d/{displaykey}?preview=",
                              Hidden = up.Hidden,
                              DisplayName = up.DisplayName,
                              UploadDate = up.UploadDate,
                              ValidUntil = up.ValidUntil,
                              MaxViews = up.MaxViews,
                              Views = up.Views
                          };

            Packet resp = new Packet();
            resp.Command = "UPLOADS";
            resp.Headers.Add("count", count.ToString());
            resp.Payload = RT.Util.Serialization.ClassifyJson.Serialize(uploads.ToArray()).ToString();
            Write(resp);
        }
        #endregion

        #region Upload packets
        [PacketHandlerFactory.PacketHandler("UPLOAD")]
        internal void UPLOAD(Packet p)
        {
            if (!(new[] { "display-name", "data-hash" }.All(h => p.Headers.ContainsKey(h)) && p.HasPayload))
                throw new InvalidOperationException("Request missing required packet headers.");

            Upload upload = new Upload();
            string dataHash = p.Headers["data-hash"];
            bool direct = p.Headers.ContainsKey("direct") && ExactConvert.To<bool>(p.Headers["direct"]);
            bool hidden = p.Headers.ContainsKey("direct") && ExactConvert.To<bool>(p.Headers["direct"]);
            string displayName = p.Headers["display-name"];
            string mimetype = System.Web.MimeMapping.GetMimeMapping(displayName);
            //string mimetype = MimeHandlers.MimeHelper.GetMimeType()
            upload.Hidden = hidden;
            upload.LastAccessed = DateTime.Now;
            upload.UploadDate = DateTime.Now;
            upload.Views = 0;
            upload.MimeType = mimetype;
            if (p.Headers.ContainsKey("view-limit"))
                upload.MaxViews = ExactConvert.To<int>(p.Headers["view-limit"]);
            if (p.Headers.ContainsKey("valid-for"))
                upload.ValidUntil = DateTime.Now + TimeSpan.FromTicks(ExactConvert.To<long>(p.Headers["valid-for"]));

            _uow.Add(upload);
            string displayKey = upload.Id.ToArbitraryBase(36);
            if (Path.GetFileNameWithoutExtension(displayName).EqualsNoCase("clowd-default"))
                upload.DisplayName = displayKey + Path.GetExtension(displayName);
            else
                upload.DisplayName = displayName;

            string azureKey = RandomEx.GetCryptoUniqueString(32);

            ModelTypes.AzureContainer container = ModelTypes.AzureContainer.Private;
            upload.Container = container;
            CloudBlockBlob blockUpload = AzureStorageClient.Current[container]
                .GetBlockBlobReference(azureKey);
            upload.StorageKey = azureKey;
            //blockUpload.Properties.ContentDisposition = "attachment; filename=" + upload.DisplayName;
            blockUpload.Properties.ContentType = mimetype;
            if (p.Headers.ContainsKey("partitioned") && ExactConvert.To<bool>(p.Headers["partitioned"]))
            {
                UploadContext contx = new UploadContext
                {
                    DataHash = dataHash,
                    DbObject = upload,
                    FileSize = ExactConvert.To<long>(p.Headers["file-size"]),
                    PayloadStream = new MultiStream(false),
                    AzureBlob = blockUpload
                };
                contx.PayloadStream.MinStreamLength = contx.FileSize;
                contx.PayloadStream.Write(p.PayloadBytes);
                contx.AzureBlobUploadTask = blockUpload.UploadFromStreamAsync(contx.PayloadStream.CloneStream());
                _uploadsInProgress.Add(contx);
                _context = contx;
                _nextPacket = "CHUNK";
                LogTo.Debug(_remoteIP + ": (" + displayKey + ") File upload in progress");
                Packet re = new Packet { Command = "CONTINUE" };
                re.Headers.Add("display-name", upload.DisplayName);
                re.Headers.Add("display-key", displayKey);
                re.Payload = _svcContext.Config.ExternalEndpoint + "/u/" + displayKey;
                Write(re);
            }
            else
            {
                byte[] content = p.PayloadBytes;
                if (!MD5.Compute(content).EqualsNoCase(dataHash))
                    throw new InvalidDataException("Payload doesn't equal precomputed hash");
                blockUpload.UploadFromByteArray(content, 0, content.Length);
                Packet resp = new Packet();
                resp.Command = "COMPLETE";
                resp.Headers.Add("display-name", upload.DisplayName);
                resp.Headers.Add("display-key", displayKey);
                resp.Payload = String.Format("{0}/{1}/{2}", _svcContext.Config.ExternalEndpoint, direct ? "d" : "u", upload.Id.ToArbitraryBase(36));
                if (_user != null)
                    _user.Uploads.Add(upload);
                _uow.SaveChanges();
                Write(resp);
                LogTo.Info(_remoteIP + ": (" + displayKey + ") New file uploaded.");
                MimeBasedUploadHandler.FinalizeUploadMetadata(upload.MimeType, upload.DisplayName, content, blockUpload, true);
            }
        }

        [PacketHandlerFactory.PacketHandler("CHUNK")]
        internal void CHUNK(Packet p)
        {
            if (!(new[] { "last" }.All(h => p.Headers.ContainsKey(h)) && p.HasPayload))
                throw new InvalidOperationException("Request missing required packet headers.");

            if (_context == null)
                throw new InvalidOperationException("No upload in progress");

            var last = ExactConvert.To<bool>(p.Headers["last"]);

            LogTo.Trace(_remoteIP + ": (" + _context.DbObject.Id.ToArbitraryBase(36) + ") File upload in progress");

            if (!last) // if not last chunk
            {
                _context.PayloadStream.Write(p.PayloadBytes);
            }
            else // this is the last chunk
            {
                _nextPacket = null;
                _context.PayloadStream.Write(p.PayloadBytes);
                // _context.PayloadStream.WriteByte(0x1A); // end of file byte ?? is this needed??

                var content = _context.PayloadStream.GetInternalMemoryStream().ToArray();
                if (!MD5.Compute(content).EqualsNoCase(_context.DataHash))
                    throw new InvalidDataException("Payload doesn't equal precomputed hash");
                _context.PayloadStream.Dispose();
                _context.AzureBlobUploadTask.Wait();
                string displayKey = _context.DbObject.Id.ToArbitraryBase(36);
                Packet resp = new Packet();
                resp.Command = "COMPLETE";
                resp.Headers.Add("display-name", _context.DbObject.DisplayName);
                resp.Headers.Add("display-key", displayKey);
                resp.Payload = String.Format("{0}/{1}/{2}", _svcContext.Config.ExternalEndpoint, _context.Direct ? "d" : "u", displayKey);
                if (_user != null)
                    _user.Uploads.Add(_context.DbObject);
                _uow.SaveChanges();
                Write(resp);
                LogTo.Debug(_remoteIP + ": (" + displayKey + ") New file uploaded.");
                MimeBasedUploadHandler.FinalizeUploadMetadata(_context.DbObject.MimeType, _context.DbObject.DisplayName, content, _context.AzureBlob, true);
                _uploadsInProgress.Remove(_context);
                _context = null;
            }
        }

        #endregion

        #region Networking stuff
        private void WriteErrorAndClose(string error, bool logError = true)
        {
            _validConnection = false;
            Packet err = new Packet();
            err.Command = "ERROR";
            err.Payload = error;
            Write(err, false);
            if (logError)
            {
                LogTo.Warn(_remoteIP + ": Error - " + error);
            }
            if (_context != null && _uploadsInProgress.Contains(_context))
            {
                _uploadsInProgress.Remove(_context);
                _context.PayloadStream.DisposeChildren = true;
                _context.PayloadStream.Dispose();
                _context = null;
            }
        }
        private bool Write(Packet p, bool logException = true)
        {
            try
            {
                var data = p.Serialize();
                _tcp.GetStream().Write(data);
                return true;
            }
            catch (Exception e)
            {
                if (logException)
                    LogTo.Warn(_remoteIP + ": Write Exception - " + e.Message);
                _validConnection = false;
                return false;
            }
        }

        private Task ReadLoopTcpClient(TcpClient client, CancellationToken token, Action<Packet> handlePacket)
        {
            return Task.Factory.StartNew(() =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    while (!token.IsCancellationRequested && _validConnection)
                    {
                        string command = stream.ReadUntil((byte)'\n', false).FromUtf8();
                        if (String.IsNullOrWhiteSpace(command)) break;
                        var tmpDict = new SortedDictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
                        string tmpHeader;
                        while (!String.IsNullOrWhiteSpace(tmpHeader = stream.ReadUntil((byte)'\n', false).FromUtf8()))
                        {
                            var split = tmpHeader.Split(':');
                            tmpDict.Add(split[0].Trim(), split[1].Trim());
                        }
                        byte[] payload = new byte[0];
                        if (tmpDict.ContainsKey(contentHeader))
                        {
                            var contentlength = Convert.ToInt32(tmpDict[contentHeader]);
                            payload = new byte[contentlength];
                            stream.FillBuffer(payload, 0, contentlength);
                        }
                        Packet p = new Packet(command, tmpDict, payload);
                        handlePacket(p);
                    }
                }
            }, token);
        }
        #endregion

        public class UploadContext
        {
            public CloudBlockBlob AzureBlob { get; set; }
            public Task AzureBlobUploadTask { get; set; }
            public Upload DbObject { get; set; }
            public long FileSize { get; set; }
            public string DataHash { get; set; }
            public MultiStream PayloadStream { get; set; }
            public bool Direct { get; set; }
        }
    }
}
