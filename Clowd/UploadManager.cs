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
using System.Windows;
using Ookii.Dialogs.Wpf;
using Clowd.Utilities;
using Ionic.Zip;
using System.IO.Compression;
using Clowd;
using Clowd.Upload;

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

        public class UploadViewState
        {
            public UploadViewState(UploadTaskViewItem taskView, Task<UploadResult> uploadResult)
            {
                TaskView = taskView;
                UploadResult = uploadResult;
            }

            public UploadTaskViewItem TaskView { get; }
            public Task<UploadResult> UploadResult { get; }
        }

        public static Task<UploadViewState> UploadImage(Stream fileStream, string extension, string name = null, string viewName = null)
        {
            return UploadInternal(SupportedUploadType.Image, fileStream, extension, name, viewName);
        }

        public static Task<UploadViewState> UploadVideo(Stream fileStream, string extension, string name = null, string viewName = null)
        {
            return UploadInternal(SupportedUploadType.Video, fileStream, extension, name, viewName);
        }

        public static Task<UploadViewState> UploadText(Stream fileStream, string extension, string name = null, string viewName = null)
        {
            return UploadInternal(SupportedUploadType.Text, fileStream, extension, name, viewName);
        }

        public static Task<UploadViewState> UploadFiles(params string[] filePaths)
        {
            IMimeProvider mimedb = new MimeDbMimeProvider();

            if (filePaths.Length == 1 && File.Exists(filePaths[0]))
            {
                var path = Path.GetFullPath(filePaths[0]);
                var info = new FileInfo(path);
                var ext = Path.GetExtension(path);
                var mime = mimedb.GetMimeFromExtension(ext);
                var category = mimedb.GetCategoryFromExtension(ext);

                SupportedUploadType supported = SupportedUploadType.Binary;
                switch (category)
                {
                    case ContentCategory.Image:
                        supported = SupportedUploadType.Image;
                        break;
                    case ContentCategory.Text:
                        supported = SupportedUploadType.Text;
                        break;
                    case ContentCategory.Video:
                        supported = SupportedUploadType.Video;
                        break;
                }

                // zip the single file if:
                // - the file type is unknown / is not a special type like image (can not be rendered nicely in browser)
                // - we think the mime type might be compressible
                // - the file size is > 5mb
                var compress = supported == SupportedUploadType.Binary && mime.Compressible != false && info.Length > 1024 * 1024 * 5;

                if (!compress)
                {
                    return UploadInternal(supported, File.OpenRead(path), ext, Path.GetFileNameWithoutExtension(path), Path.GetFileName(path));
                }
            }

            return DoZipUpload(filePaths, mimedb);
        }

        private static async Task<UploadViewState> DoZipUpload(string[] filePaths, IMimeProvider mimedb)
        {
            throw new NotImplementedException();
        }

        private static async Task<UploadViewState> UploadInternal(SupportedUploadType type, Stream fileStream, string extension, string name = null, string viewName = null)
        {
            if (viewName == null)
                viewName = type.ToString() + " File";

            if (name == null)
                name = CS.Util.RandomEx.GetString(8).ToLower();

            extension = extension.Trim('.');
            var fileName = $"{name}.{extension}";

            var tcs = new CancellationTokenSource();
            var view = new UploadTaskViewItem(viewName, "Starting...", tcs);
            _window.AddTask(view);

            try
            {
                var provider = await GetUploadProvider(type);
                if (provider == null)
                    throw new Exception("No available provider");

                var data_size = fileStream.Length;

                view.SecondaryText = "Uploading...";
                view.ProgressTargetText = data_size.ToPrettySizeString(0);

                UploadProgressHandler handler = (bytesUploaded) =>
                {
                    var progress = (bytesUploaded / (double)data_size) * 100;
                    _window.Dispatcher.Invoke(() =>
                    {
                        view.ProgressCurrentText = ((long)Math.Min(bytesUploaded, data_size)).ToPrettySizeString(0);
                        view.Progress = progress > 98 ? 98 : progress;
                    });
                };

                var uploadTask = provider.UploadAsync(fileStream, handler, fileName, tcs.Token).ContinueWith<UploadResult>(task =>
                {
                    if (task.IsFaulted)
                    {
                        view.Status = TaskViewItem.TaskStatus.Error;
                        view.Progress = 99;
                        view.SecondaryText = task.Exception.Message;
                        return null;
                    }
                    else
                    {
                        var result = task.Result;
                        view.UploadURL = result.PublicUrl;
                        view.SecondaryText = "Complete";
                        view.Progress = 100;
                        view.ProgressCurrentText = data_size.ToPrettySizeString(0);
                        return result;
                    }
                });

                return new UploadViewState(view, uploadTask);
            }
            catch (Exception e)
            {
                view.Status = TaskViewItem.TaskStatus.Error;
                view.Progress = 99;
                view.SecondaryText = e.Message;

                return new UploadViewState(view, Task.FromResult<UploadResult>(null));
            }
        }

        private static async Task<IUploadProvider> GetUploadProvider(SupportedUploadType type)
        {
            var settings = App.Current.Settings.UploadSettings;
            IUploadProvider provider;

            switch (type)
            {
                case SupportedUploadType.Image:
                    provider = settings.Image;
                    break;
                case SupportedUploadType.Video:
                    provider = settings.Video;
                    break;
                case SupportedUploadType.Text:
                    provider = settings.Text;
                    break;
                case SupportedUploadType.Binary:
                    provider = settings.Binary;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }

            if (provider != null && provider.IsEnabled)
                return provider;

            var enabled = settings.GetEnabledProviders(type).ToArray();

            if (enabled.Length > 0)
            {
                using (TaskDialog dialog = new TaskDialog())
                {
                    dialog.WindowTitle = type.ToString() + " Upload";
                    dialog.MainInstruction = $"Select an upload destination:";
                    dialog.Content = $"You have not selected a default upload provider for '{type}', where would you like to send your file?";
                    dialog.ButtonStyle = TaskDialogButtonStyle.CommandLinks;

                    Dictionary<TaskDialogButton, IUploadProvider> providerLookup = new Dictionary<TaskDialogButton, IUploadProvider>();

                    foreach (var p in enabled)
                    {
                        TaskDialogButton btn = new TaskDialogButton(p.Name);
                        btn.CommandLinkNote = p.Description;
                        dialog.Buttons.Add(btn);
                        providerLookup[btn] = p;
                    }

                    dialog.AllowDialogCancellation = true;
                    dialog.VerificationText = "Set choice as default";

                    var dialogResult = await dialog.ShowAsNiceDialogAsync(null);

                    if (dialogResult != null && providerLookup.ContainsKey(dialogResult))
                    {
                        var lookup = providerLookup[dialogResult];
                        return lookup;
                    }

                    return null;
                }
            }
            else
            {
                await NiceDialog.ShowSettingsPromptAsync(null, SettingsCategory.Uploads,
                    $"There is no upload provider configured/enabled for '{type}'. Please visit settings to configure before uploading.");

                return null;
            }
        }

        public static void ShowWindow()
        {
            _window.Show();
        }
    }
}
