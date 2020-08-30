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
using System.Windows;
using Ookii.Dialogs.Wpf;
using Clowd.Utilities;
using Ionic.Zip;
using System.IO.Compression;

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

        public static async Task<string> Upload(ZipFile zip, long estimatedSize, string extension, string viewName, string fileName, bool autoExecute = false)
        {
            var uploader = await GetProvider();
            InternalUploadDelegate func = async (name, updateProgress) =>
            {
                using (var uploadStream = uploader.BeginLargeUpload(name, false))
                {
                    Dictionary<string, long> byteCounter = new Dictionary<string, long>();
                    zip.SaveProgress += (s, e) =>
                    {
                        if (e.CurrentEntry != null)
                        {
                            if (!byteCounter.ContainsKey(e.CurrentEntry.FileName))
                                byteCounter[e.CurrentEntry.FileName] = 0;

                            byteCounter[e.CurrentEntry.FileName] = Math.Max(byteCounter[e.CurrentEntry.FileName], e.BytesTransferred);
                        }

                        var totalBytesWritten = Math.Min(byteCounter.Values.Sum(), estimatedSize - 1);
                        updateProgress(totalBytesWritten);
                    };

                    await Task.Run(() =>
                    {
                        zip.Save(uploadStream);
                    });

                    return await uploader.EndLargeUpload(uploadStream);
                }
            };

            return await UploadInternal(func, estimatedSize, extension, viewName, fileName, autoExecute);
        }

        public static async Task<string> Upload(Stream data, string extension, string viewName, string fileName, bool autoExecute = false)
        {
            var uploader = await GetProvider();
            var data_size = data.Length;
            InternalUploadDelegate func;

            if (data_size > (1024 * 1024 * 4)) // > 4MB 
            {
                func = async (name, updateProgress) =>
                {
                    using (var uploadStream = uploader.BeginLargeUpload(name, true))
                    {
                        using (var progressStream = new ProgressStream(data))
                        using (GZipStream compress = new GZipStream(uploadStream, CompressionMode.Compress, true))
                        {
                            progressStream.BytesReadEvent += (s, e) => updateProgress(e.BytesRead);
                            await progressStream.CopyToAsync(compress);
                        }

                        return await uploader.EndLargeUpload(uploadStream);
                    }
                };
            }
            else
            {
                func = (name, updateProgress) => uploader.Upload(data, name, (bytes) => updateProgress(bytes));
            }

            return await UploadInternal(func, data_size, extension, viewName, fileName, autoExecute);
        }

        private delegate Task<UploadResult> InternalUploadDelegate(string fileName, ProgressHandler updateProgress);

        private static async Task<string> UploadInternal(InternalUploadDelegate start, long estimatedSize, string extension, string viewName, string fileName, bool autoExecute)
        {
            if (String.IsNullOrWhiteSpace(extension))
                throw new ArgumentNullException(nameof(extension));

            extension = extension.Trim('.').Trim();

            if (String.IsNullOrWhiteSpace(viewName))
                viewName = "Upload";

            if (String.IsNullOrWhiteSpace(fileName))
                fileName = CS.Util.RandomEx.GetString(8);

            var canceler = new ManualResetEventSlim(false);
            var view = new UploadTaskViewItem(viewName, "Connecting...", canceler);
            _window.AddTask(view);

            try
            {
                var data_size = estimatedSize;

                view.SecondaryText = "Uploading...";
                view.ProgressTargetText = ((long)data_size).ToPrettySizeString(0);

                var result = await start($"{fileName}.{extension}", (bytesUploaded) =>
                {
                    var progress = (bytesUploaded / (double)data_size) * 100;
                    _window.Dispatcher.Invoke(() =>
                    {
                        view.ProgressCurrentText = ((long)Math.Min(bytesUploaded, data_size)).ToPrettySizeString(0);
                        view.Progress = progress > 98 ? 98 : progress;
                    });
                });

                view.UploadURL = result.PublicUrl;
                view.SecondaryText = "Complete";
                view.Progress = 100;
                view.ProgressCurrentText = ((long)data_size).ToPrettySizeString(0);

                if (autoExecute)
                {
                    view.SetExecuted();
                }
                else
                {
                    _window.Notify();
                }

                return result.PublicUrl;
            }
            catch (Exception e)
            {
                view.Status = TaskViewItem.TaskStatus.Error;
                view.Progress = 99;
                view.SecondaryText = e.Message;
                return null;
            }
        }

        private static async Task<IUploadProvider> GetProvider()
        {
            IUploadProvider uploader;
            var providerSelection = App.Current.Settings.UploadSettings.UploadProvider;
            if (providerSelection == UploadsProvider.None)
            {
                await NiceDialog.ShowSettingsPromptAsync(null, SettingsCategory.Uploads, "There is no uploads provider configured. Please open settings and configure before uploading files.");
                return null;
            }
            else if (providerSelection == UploadsProvider.Azure)
            {
                uploader = new AzureProvider(App.Current.Settings.UploadSettings);
            }
            else
            {
                throw new NotImplementedException();
            }
            return uploader;
        }

        public static void ShowWindow()
        {
            var providerSelection = App.Current.Settings.UploadSettings.UploadProvider;
            if (providerSelection == UploadsProvider.None)
            {
                return;
            }
            _window.Show();
        }
    }
}
