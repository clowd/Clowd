using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using RT.Util.ExtensionMethods;
using Clowd.Shared;

namespace Clowd
{
    public static class UploadManager
    {
        public static int UploadsInProgress { get { return window.Value.Uploads.Where(up => up.Progress < 100 && up.UploadFailed == false).Count(); } }
        public static bool UploadWindowVisible { get { return window.Value.IsVisible; } }
        public static bool SignedIn { get { return sessionKey != null || loginDetails != null; } }

        private const string contentHeader = "CONTENT-LENGTH";
        private static readonly TimeSpan packetTimeout = TimeSpan.FromSeconds(10);
        private static Lazy<UploadsWindow> window = new Lazy<UploadsWindow>();
        private static string sessionKey = "";
        private static Tuple<string, string> loginDetails;

        //public static async Task<string> UploadText(string text, bool highlight = false, UploadOptions options = null)
        //{
        //    var uploadBar = CreateNewUpload("Text Paste");
        //    UploadSession context;
        //    try
        //    {
        //        using (context = await GetSession(true))
        //        {
        //            UpdateUploadProgress(uploadBar, 25, 1);
        //            Packet p = new Packet();
        //            p.Command = "UPLOAD";
        //            p.Headers.Add("content-type", "txt");
        //            p.Headers.Add("data-hash", MD5.Compute(text));
        //            p.Headers.Add("highlight", highlight ? "true" : "false");

        //            if (!String.IsNullOrEmpty(options?.Password))
        //                p.Headers.Add("password", options.Password);
        //            if (options?.ViewLimit > 0)
        //                p.Headers.Add("view-limit", options.ViewLimit.ToString());
        //            if (options?.ValidDuration != null)
        //                p.Headers.Add("valid-for", options.ValidDuration.Value.Ticks.ToString());

        //            p.Payload = text;
        //            int byteLength = p.PayloadBytes.Length;
        //            await context.WriteAsync(p);
        //            //TODO: Add Timeout
        //            UpdateUploadProgress(uploadBar, 75, (byteLength / 3) * 2);
        //            var response = await context.PacketBuffer.ReceiveAsync();
        //            if (response.Command == "COMPLETE" && response.HasPayload)
        //            {
        //                uploadBar.ActionLink = response.Payload;
        //                uploadBar.ActionAvailable = true;
        //                UpdateUploadProgress(uploadBar, 100, byteLength);
        //                return response.Payload;
        //            }
        //            else
        //            {
        //                throw new NotImplementedException();
        //            }
        //        }
        //    }
        //    catch (Exception e)
        //    {
        //        ErrorUploadProgress(uploadBar, e.Message);
        //        return null;
        //    }
        //}
        //public static async Task<string> UploadImage(byte[] data, string displayName, bool allowDirect = false, UploadOptions options = null)
        //{
        //    var uploadBar = CreateNewUpload(displayName);
        //    UploadSession context;
        //    try
        //    {
        //        using (context = await GetSession(true))
        //        {
        //            UpdateUploadProgress(uploadBar, 20, 1000);

        //            Packet p = new Packet();
        //            p.Command = "UPLOAD";
        //            p.Headers.Add("content-type", "img");
        //            p.Headers.Add("data-hash", MD5.Compute(data));
        //            p.Headers.Add("display-name", displayName);
        //            p.Headers.Add("direct", allowDirect ? "true" : "false");

        //            if (!String.IsNullOrEmpty(options?.Password))
        //                p.Headers.Add("password", options.Password);
        //            if (options?.ViewLimit > 0)
        //                p.Headers.Add("view-limit", options.ViewLimit.ToString());
        //            if (options?.ValidDuration != null)
        //                p.Headers.Add("valid-for", options.ValidDuration.Value.Ticks.ToString());

        //            p.PayloadBytes = data;
        //            await context.WriteAsync(p);
        //            UpdateUploadProgress(uploadBar, 65, data.Length / 2);
        //            var response = await context.PacketBuffer.ReceiveAsync();
        //            if (response.Command == "COMPLETE" && response.HasPayload)
        //            {
        //                if (response.Headers.ContainsKey("display-name"))
        //                    uploadBar.DisplayText = response.Headers["display-name"];
        //                uploadBar.ActionLink = response.Payload;
        //                uploadBar.ActionAvailable = true;
        //                UpdateUploadProgress(uploadBar, 100, data.Length);
        //                return response.Payload;
        //            }
        //            else
        //            {
        //                throw new NotImplementedException();
        //            }
        //        }
        //    }
        //    catch (Exception e)
        //    {
        //        ErrorUploadProgress(uploadBar, e.Message);
        //        return null;
        //    }
        //}
        //public static async Task<string> UploadFile(byte[] data, string displayName, UploadOptions options = null)
        //{
        //    var uploadBar = CreateNewUpload(displayName);
        //    UploadSession context;
        //    try
        //    {
        //        using (context = await GetSession(true))
        //        {
        //            Packet p = new Packet();
        //            p.Command = "UPLOAD";
        //            p.Headers.Add("content-type", "file");
        //            p.Headers.Add("data-hash", MD5.Compute(data));
        //            p.Headers.Add("display-name", displayName);

        //            if (!String.IsNullOrEmpty(options?.Password))
        //                p.Headers.Add("password", options.Password);
        //            if (options?.ViewLimit > 0)
        //                p.Headers.Add("view-limit", options.ViewLimit.ToString());
        //            if (options?.ValidDuration != null)
        //                p.Headers.Add("valid-for", options.ValidDuration.Value.Ticks.ToString());

        //            const int chunk_size = 65535;
        //            int data_size = data.Count();

        //            if (data_size > chunk_size)
        //            {
        //                p.Headers.Add("partitioned", "true");
        //                p.Headers.Add("file-size", data_size.ToString());

        //                for (int i = 0; i < data_size; i += chunk_size)
        //                {
        //                    var size = Math.Min(chunk_size, data_size - i);
        //                    bool last = i + chunk_size >= data_size;
        //                    byte[] buffer = new byte[size];
        //                    double progress = ((double)i + size) / data_size * 100;
        //                    Buffer.BlockCopy(data, i, buffer, 0, size);

        //                    if (i == 0)
        //                    {
        //                        p.PayloadBytes = buffer;
        //                        await context.WriteAsync(p);
        //                        var initial = await context.PacketBuffer.ReceiveAsync();
        //                        if (initial.Command != "CONTINUE")
        //                            throw new NotImplementedException();
        //                        if (initial.Headers.ContainsKey("display-name"))
        //                            uploadBar.DisplayText = initial.Headers["display-name"];
        //                        uploadBar.ActionLink = initial.Payload;
        //                        uploadBar.ActionAvailable = true;
        //                    }
        //                    else
        //                    {
        //                        //await Task.Delay(2000); //remove, this is for testing.
        //                        Packet chk = new Packet();
        //                        chk.Command = "CHUNK";
        //                        chk.Headers.Add("last", last ? "true" : "false");
        //                        chk.PayloadBytes = buffer;
        //                        await context.WriteAsync(chk);
        //                    }
        //                    UpdateUploadProgress(uploadBar, progress, i + chunk_size);
        //                }
        //            }
        //            else
        //            {
        //                p.PayloadBytes = data;
        //                UpdateUploadProgress(uploadBar, 50, data.Length / 2);
        //                await context.WriteAsync(p);
        //            }
        //            var response = await context.PacketBuffer.ReceiveAsync();
        //            if (response.Command == "COMPLETE" && response.HasPayload)
        //            {
        //                if (response.Headers.ContainsKey("display-name"))
        //                    uploadBar.DisplayText = response.Headers["display-name"];
        //                UpdateUploadProgress(uploadBar, 100, data.Length);
        //                uploadBar.ActionLink = response.Payload;
        //                uploadBar.ActionAvailable = true;
        //                return response.Payload;
        //            }
        //            else
        //                throw new NotImplementedException();
        //        }
        //    }
        //    catch (Exception e)
        //    {
        //        ErrorUploadProgress(uploadBar, e.Message);
        //        return null;
        //    }
        //}

        public static async Task<string> Upload(byte[] data, string displayName, UploadOptions options = null)
        {
            var uploadBar = CreateNewUpload(displayName);
            UploadSession context;
            try
            {
                using (context = await GetSession(true))
                {
                    Packet p = new Packet();
                    p.Command = "UPLOAD";
                    p.Headers.Add("content-type", "file");
                    p.Headers.Add("data-hash", MD5.Compute(data));
                    p.Headers.Add("display-name", displayName);

                    if (options?.Direct == true)
                        p.Headers.Add("direct", "true");
                    if (options?.ViewLimit > 0)
                        p.Headers.Add("view-limit", options.ViewLimit.ToString());
                    if (options?.ValidDuration != null)
                        p.Headers.Add("valid-for", options.ValidDuration.Value.Ticks.ToString());

                    const int chunk_size = 65535;
                    int data_size = data.Count();

                    if (data_size > chunk_size)
                    {
                        p.Headers.Add("partitioned", "true");
                        p.Headers.Add("file-size", data_size.ToString());

                        for (int i = 0; i < data_size; i += chunk_size)
                        {
                            var size = Math.Min(chunk_size, data_size - i);
                            bool last = i + chunk_size >= data_size;
                            byte[] buffer = new byte[size];
                            double progress = ((double)i + size) / data_size * 100;
                            Buffer.BlockCopy(data, i, buffer, 0, size);

                            if (i == 0)
                            {
                                p.PayloadBytes = buffer;
                                await context.WriteAsync(p);
                                var initial = await context.PacketBuffer.ReceiveAsync();
                                if (initial.Command != "CONTINUE")
                                    throw new NotImplementedException();
                                if (initial.Headers.ContainsKey("display-name"))
                                    uploadBar.DisplayText = initial.Headers["display-name"];
                                uploadBar.ActionLink = initial.Payload;
                                uploadBar.ActionAvailable = true;
                            }
                            else
                            {
                                //await Task.Delay(500); //remove, this is for testing.
                                Packet chk = new Packet();
                                chk.Command = "CHUNK";
                                chk.Headers.Add("last", last ? "true" : "false");
                                chk.PayloadBytes = buffer;
                                await context.WriteAsync(chk);
                            }
                            UpdateUploadProgress(uploadBar, progress > 98 ? 98 : progress, i + chunk_size);
                        }
                    }
                    else
                    {
                        p.PayloadBytes = data;
                        UpdateUploadProgress(uploadBar, 50, data.Length / 2);
                        await context.WriteAsync(p);
                    }
                    var response = await context.PacketBuffer.ReceiveAsync();
                    if (response.Command == "COMPLETE" && response.HasPayload)
                    {
                        if (response.Headers.ContainsKey("display-name"))
                            uploadBar.DisplayText = response.Headers["display-name"];
                        UpdateUploadProgress(uploadBar, 100, data.Length);
                        if (!UploadWindowVisible)
                            ShowUploadsWindow();
                        uploadBar.ActionLink = response.Payload;
                        uploadBar.ActionAvailable = true;
                        return response.Payload;
                    }
                    else
                        throw new NotImplementedException();
                }
            }
            catch (Exception e)
            {
                ErrorUploadProgress(uploadBar, e.Message);
                return null;
            }
        }

        public static async Task<UploadDTO[]> MyUploads(int offset = 0, int count = 10)
        {
            try
            {
                using (var context = await GetSession(true))
                {
                    Packet pa = new Packet("MYUPLOADS");
                    pa.Headers.Add("count", count.ToString());
                    pa.Headers.Add("offset", offset.ToString());
                    await context.WriteAsync(pa);
                    var resp = await context.PacketBuffer.ReceiveAsync();
                    return RT.Util.Serialization.ClassifyJson.Deserialize<UploadDTO[]>(RT.Util.Json.JsonValue.Parse(resp.Payload));
                }
            }
            catch
            {
                return null;
            }
        }


        public static void RemoveUpload(string url, bool closeWindowIfOnly = true)
        {
            var search = window.Value.Uploads.SingleOrDefault(up => up.ActionLink == url);
            if (search != null)
            {
                window.Value.Uploads.Remove(search);
                if (closeWindowIfOnly && !window.Value.Uploads.Any(up => up.Progress < 100))
                {
                    window.Value.Hide();
                }
            }
        }
        public static void ShowUploadsWindow()
        {
            window.Value.Show();
            if (!window.Value.Uploads.Any(up => up.Progress < 100))
                window.Value.CloseTimerEnabled = true;
        }


        public static async Task<LoginResult> Login(Tuple<string, string> login = null)
        {
            using (var session = await GetSession(false))
            {
                var result = await session.Authenticate(login);
                if (result == LoginResult.Valid)
                {
                    loginDetails = login;
                }
                return result;
            }
        }
        public static void Logout()
        {
            loginDetails = null;
            sessionKey = null;
        }


        private static async Task<UploadSession> GetSession(bool login = true)
        {
            var session = await UploadSession.GetSession();
            if (session == null)
                return null;

            if (login && (sessionKey != null || loginDetails != null))
            {
                var authResult = await session.Authenticate();
            }

            return session;
        }



        private static Controls.UploadProgressBar CreateNewUpload(string display)
        {
            var cnt = new Controls.UploadProgressBar();
            cnt.Foreground = System.Windows.Media.Brushes.PaleGoldenrod;
            cnt.DisplayText = "Connecting...";
            window.Value.Uploads.Add(cnt);
            window.Value.Show();
            window.Value.CloseTimerEnabled = false;
            return cnt;
        }
        private static void UpdateUploadProgress(Controls.UploadProgressBar upload, double progress, long bytesWritten)
        {
            if (progress >= 100)
            {
                upload.ActionAvailable = true;
                upload.Foreground = System.Windows.Media.Brushes.PaleGreen;
            }
            upload.Progress = progress;
            upload.CurrentSizeDisplay = bytesWritten.ToPrettySizeString(0);
        }
        private static void ErrorUploadProgress(Controls.UploadProgressBar upload, string message)
        {
            upload.Foreground = System.Windows.Media.Brushes.PaleVioletRed;
            upload.UploadFailed = true;
            upload.ActionClicked = true;
            upload.Progress = 100;
        }


        private class UploadSession : IDisposable
        {
            public CancellationTokenSource CancelToken { get; private set; }
            public NetworkStream WriteStream { get; private set; }
            public BufferBlock<Packet> PacketBuffer { get; private set; }

            public bool Connected
            {
                get { return _client.Connected && !_readLoop.IsCompleted; }
            }
            public bool Authenticated { get; private set; } = false;

            public string Error { get; private set; }

            private TcpClient _client;
            private Task _readLoop;
            private static UploadSession _keep;
            private static readonly object _keepLock = new object();
            private static System.Windows.Threading.DispatcherTimer _idle;

            private UploadSession(TcpClient client)
            {
                _client = client;
                WriteStream = _client.GetStream();
                CancelToken = new CancellationTokenSource();
                PacketBuffer = new BufferBlock<Packet>();
                _readLoop = ReadLoopTcpClient(_client, CancelToken.Token, PacketBuffer);
                _readLoop.ContinueWith(rl => Dispose(true));
            }
            public static async Task<UploadSession> GetSession()
            {
                lock (_keepLock)
                {
                    if (_keep != null)
                    {
                        var tmp = _keep;
                        _keep = null;
                        _idle.Stop();
                        _idle = null;
                        if (tmp.Connected)
                            return tmp;
                        else
                            tmp.Dispose(true);
                    }
                }
                TcpClient tcp = new TcpClient();
                try
                {
                    await tcp.ConnectAsync(App.ServerHost, 12998);
                }
                catch { return null; }
                return new UploadSession(tcp);
            }
            public async Task<LoginResult> Authenticate(Tuple<string, string> login = null)
            {
                if (Authenticated)
                    return LoginResult.Valid;
                try
                {
                    if (!String.IsNullOrEmpty(sessionKey) && login == null)
                    {
                        await this.WriteAsync(new Packet() { Command = "SESSIONCK", Payload = sessionKey });
                        var check = await this.PacketBuffer.ReceiveAsync(packetTimeout);
                        if (check == null || !check.Command.Equals("SESSIONCKRESP"))
                            return LoginResult.NetworkError;
                        if (check.Headers.ContainsKey("valid") && (check.Headers["valid"] == "1" || check.Headers["valid"] == "true"))
                        {
                            Authenticated = true;
                            return LoginResult.Valid;
                        }
                    }
                    sessionKey = null;
                    if (login == null)
                        login = loginDetails;
                    if (login == null || String.IsNullOrEmpty(login.Item1) || String.IsNullOrEmpty(login.Item2))
                        return LoginResult.NoLoginSaved;

                    await this.WriteAsync(new Packet() { Command = "LOGIN", Payload = login.Item1 });
                    var challenge = await this.PacketBuffer.ReceiveAsync(packetTimeout);
                    if (challenge == null || !challenge.Command.Equals("LOGINRESP"))
                        return LoginResult.NetworkError;
                    if (challenge.Headers.ContainsKey("error"))
                        return LoginResult.InvalidUserOrPass;

                    string iv = challenge.Headers["iv"];
                    string salt = challenge.Headers["salt"];
                    string authstring = MD5.Compute(MD5.Compute(login.Item2, salt), iv);

                    await this.WriteAsync(new Packet() { Command = "AUTH", Payload = authstring });
                    var authresp = await this.PacketBuffer.ReceiveAsync(packetTimeout);
                    if (authresp == null || !authresp.Command.Equals("AUTHRESP"))
                        return LoginResult.NetworkError;
                    if (authresp.Headers.ContainsKey("error"))
                        return LoginResult.InvalidUserOrPass;
                    sessionKey = authresp.Payload;
                    Authenticated = true;
                    return LoginResult.Valid;
                }
                catch { return LoginResult.NetworkError; }
            }

            public async Task WriteAsync(Packet p)
            {
                var data = p.Serialize();
                await WriteStream.WriteAsync(data, 0, data.Length);
            }
            public void Dispose()
            {
                Dispose(false);
            }
            private void Dispose(bool forceDispose)
            {
                if (!forceDispose)
                {
                    lock (_keepLock)
                    {
                        if (_keep == null)
                        {
                            _keep = this;
                            _idle = new System.Windows.Threading.DispatcherTimer();
                            _idle.Interval = TimeSpan.FromSeconds(10);
                            _idle.Tick += (s, e) =>
                            {
                                _keep.Dispose(true);
                                _keep = null;
                                _idle.Stop();
                                _idle = null;
                            };
                            _idle.Start();
                            return;
                        }
                    }
                }
                CancelToken.Cancel();
                if (_client != null && _client.Connected)
                    _client.Close();
                WriteStream.Dispose();
                PacketBuffer.Complete();
            }
            private Task ReadLoopTcpClient(TcpClient client, CancellationToken token, BufferBlock<Packet> handlePacket)
            {
                return Task.Factory.StartNew(() =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                byte[] commandByte = stream.ReadUntil((byte)'\n', false);
                                if (commandByte == null) break;
                                string command = commandByte.FromUtf8();
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
                                if (command.EqualsNoCase("ERROR")) // this is a fatal error
                                {
                                    Error = p.Payload;
                                    break;
                                }
                                else if (p.Headers.ContainsKey("error"))
                                    Error = p.Headers["error"];
                                handlePacket.Post(p);
                            }
                        }
                        catch { }
                    }
                }, token);
            }
        }
    }
    public enum LoginResult
    {
        Valid,
        NetworkError,
        NoLoginSaved,
        InvalidUserOrPass,
    }
    public class UploadOptions
    {
        public bool Direct { get; set; } = false;
        public TimeSpan? ValidDuration { get; set; } = null;
        public int ViewLimit { get; set; } = -1;
    }
}
