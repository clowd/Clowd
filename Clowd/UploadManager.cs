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
            IUploadProvider uploader;
            var providerSelection = App.Current.Settings.UploadSettings.UploadProvider;
            if (providerSelection == UploadsProvider.None)
            {
                var config = new TaskDialogInterop.TaskDialogOptions();
                config.Title = $"{App.ClowdAppName}";
                config.MainInstruction = $"{App.ClowdAppName} File Upload Not Configured";
                config.Content =
                    $"There is no uploads provider configured in the {App.ClowdAppName} settings. Please open settings and configure before uploading files.";
                config.CommonButtons = TaskDialogInterop.TaskDialogCommonButtons.Close;
                config.MainIcon = TaskDialogInterop.VistaTaskDialogIcon.Warning;
                var response = TaskDialogInterop.TaskDialog.Show(config);
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

            string viewName = displayName;
            if (displayName.StartsWith("clowd-default", StringComparison.InvariantCultureIgnoreCase))
                viewName = "Upload";
            var canceler = new ManualResetEventSlim(false);
            var view = new UploadTaskViewItem(viewName, "Connecting...", canceler);
            _window.AddTask(view);

            try
            {
                var data_size = data.Length;

                view.SecondaryText = "Uploading...";
                view.ProgressTargetText = ((long)data_size).ToPrettySizeString(0);

                var result = await uploader.Upload(data, displayName, (bytesUploaded) =>
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
                view.Progress = 99;
                view.SecondaryText = e.Message;
                return null;
            }
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
