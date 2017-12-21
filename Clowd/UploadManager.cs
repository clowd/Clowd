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
using FileUploadLib;
using FileUploadLib.Providers;

namespace Clowd
{
    public static class UploadManager
    {
        private static TaskWindow _window
        {
            get
            {
                if (_windowBacking == null)
                {
                    _windowBacking = new TaskWindow();
                    //_windowBacking.Show();
                }
                return _windowBacking;
            }
        }
        private static TaskWindow _windowBacking;

        public static Task<string> Upload(byte[] data, string displayName)
        {
            return Upload(new MemoryStream(data), displayName);
        }

        public static async Task<string> Upload(Stream data, string displayName)
        {
            string viewName = displayName;
            if (displayName.StartsWith("clowd-default", StringComparison.InvariantCultureIgnoreCase))
                viewName = "Upload";
            var canceler = new ManualResetEventSlim(false);
            var view = new UploadTaskViewItem(viewName, "Connecting...", canceler);
            _window.AddTask(view);

            try
            {
                var uploader = new AzureProvider();

                var data_size = data.Length;

                view.SecondaryText = "Uploading...";
                view.ProgressTargetText = ((long)data_size).ToPrettySizeString(0);

                var options = new AzureOptions
                {
                    FileName = displayName,
                    ConnectionString = "DefaultEndpointsProtocol=https;AccountName=zstcs01;AccountKey=IoFdW62MlMDQrgt6jdekT9QzG+5FNKhRtVi1x2qOxSbOsYUf9oT3jLOY+HOWZ9LiyR6yqP2Fkj1/A0+4uMGzHw==;EndpointSuffix=core.windows.net",
                    ContainerName = "clowd",
                };

                var result = await uploader.Upload(data, options, (bytesUploaded) =>
                {
                    view.ProgressCurrentText = ((long)Math.Min(bytesUploaded, data_size)).ToPrettySizeString(0);
                    var progress = (bytesUploaded / (double)data_size) * 100;
                    view.Progress = progress > 98 ? 98 : progress;
                });

                view.UploadURL = result.PublicUrl;
                view.SecondaryText = "Complete";
                view.Progress = 100;
                view.ProgressCurrentText = ((long)data_size).ToPrettySizeString(0);
                _window.Notify();

                return result.PublicUrl;
            }
            catch (Exception e)
            {
                view.Status = TaskViewItem.TaskStatus.Error;
                view.SecondaryText = e.Message;
                return null;
            }
        }

        public static void ShowWindow()
        {
            _window.Show();
        }
    }
}
