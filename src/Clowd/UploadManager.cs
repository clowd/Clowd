using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clowd.Config;
using Clowd.UI;
using Clowd.UI.Helpers;
using Clowd.Upload;
using Clowd.Util;
using Ionic.Zip;
using Ookii.Dialogs.Wpf;

namespace Clowd.Capture
{
    public static class UploadManager
    {
        static ClowdSettings _settings => ClowdSettings.Current;

        private delegate Task<UploadResult> DoUploadDelegate(IUploadProvider provider, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken);

        private static readonly ITasksView _view = App.GetService<ITasksView>();

        public static Task<UploadViewState> UploadImage(Stream fileStream, string extension, string name = null, string viewName = null)
        {
            return UploadStream(SupportedUploadType.Image, fileStream, extension, name, viewName);
        }

        public static Task<UploadViewState> UploadVideo(Stream fileStream, string extension, string name = null, string viewName = null)
        {
            return UploadStream(SupportedUploadType.Video, fileStream, extension, name, viewName);
        }

        public static Task<UploadViewState> UploadText(Stream fileStream, string extension, string name = null, string viewName = null)
        {
            return UploadStream(SupportedUploadType.Text, fileStream, extension, name, viewName);
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
                    DoUploadDelegate upload = (provider, progress, uploadName, cancelToken) => provider.UploadAsync(path, progress, uploadName, cancelToken);
                    return UploadInternal(supported, upload, info.Length, ext, Path.GetFileNameWithoutExtension(path), Path.GetFileName(path));
                }
            }

            return DoZipUpload(filePaths, mimedb);
        }

        private static Task<UploadViewState> UploadStream(SupportedUploadType type, Stream fileStream, string extension, string name, string viewName)
        {
            DoUploadDelegate upload = (provider, progress, uploadName, cancelToken) => provider.UploadAsync(fileStream, progress, uploadName, cancelToken);
            return UploadInternal(SupportedUploadType.Image, upload, fileStream.Length, extension, name, viewName);
        }

        private static async Task<UploadViewState> DoZipUpload(string[] filePaths, IMimeProvider mimedb)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Binary);
            if (provider == null)
                return null;

            using (ZipFile zip = new ZipFile())
            {
                List<string> fullPaths = new List<string>();
                foreach (var path in filePaths)
                {
                    if (Directory.Exists(path))
                    {
                        zip.AddDirectory(path, Path.GetFileName(path));
                        fullPaths.Add(Path.GetFullPath(path));
                    }
                    else if (File.Exists(path))
                    {
                        zip.AddFile(path, "");
                        fullPaths.Add(Path.GetFullPath(path));
                    }
                }

                // no files were added to the archive; there is nothing to upload
                if (fullPaths.Count == 0)
                    return null;

                var view = _view.CreateTask("Archive Upload");
                view.SetStatus("Compressing...");
                view.Show();

                using (var folder = PathEx.GetTempFolder())
                {
                    var zipPath = folder.GetTempFilePath(".zip");

                    zip.SaveProgress += (s, e) =>
                    {
                        if (view.CancelToken.IsCancellationRequested)
                        {
                            e.Cancel = true;
                        }
                        else
                        {
                            Console.WriteLine($"zip - saved {e.EntriesSaved}/{e.EntriesTotal}, bytes {e.BytesTransferred}/{e.TotalBytesToTransfer},");
                            if (e.EventType == ZipProgressEventType.Saving_BeforeWriteEntry)
                            {
                                var progress = e.EntriesSaved / (double)e.EntriesTotal;
                                view.SetProgress(progress);
                                Console.WriteLine($"zip progress {progress}%");
                            }
                        }
                    };

                    await Task.Run(() => zip.Save(zipPath), view.CancelToken);

                    if (view.CancelToken.IsCancellationRequested)
                        return null;

                    var info = new FileInfo(zipPath);
                    var size = info.Length;

                    view.SetStatus("Uploading...");
                    view.SetProgress(0, size);

                    UploadProgressHandler handler = (bytesUploaded) => view.SetProgress(bytesUploaded, size);

                    var archiveName = RandomEx.GetString(8) + ".zip";
                    var uploadTask = provider.UploadAsync(zipPath, handler, archiveName, view.CancelToken);
                    return UploadWrapper(view, uploadTask);
                }
            }
        }

        private static async Task<UploadViewState> UploadInternal(SupportedUploadType type, DoUploadDelegate doUpload, long size, string extension, string name = null, string viewName = null)
        {
            if (viewName == null)
                viewName = type.ToString() + " File";

            if (name == null)
                name = RandomEx.GetString(8).ToLower();

            extension = extension.Trim('.');
            var fileName = $"{name}.{extension}";

            var provider = await GetUploadProvider(type);
            if (provider == null)
                return null;

            var view = _view.CreateTask(viewName);
            view.SetStatus("Uploading...");
            view.SetProgress(0, size);
            view.Show();

            UploadProgressHandler handler = (bytesUploaded) =>
            {
                view.SetProgress(bytesUploaded, size);
            };

            var uploadTask = doUpload(provider, handler, fileName, view.CancelToken);
            return UploadWrapper(view, uploadTask);
        }

        private static UploadViewState UploadWrapper(ITasksViewItem view, Task<UploadResult> uploadTask)
        {
            var finalTask = uploadTask.ContinueWith<UploadResult>(task =>
            {
                if (task.IsCanceled)
                {
                    view.Hide();
                    return null;
                }
                else if (task.IsFaulted)
                {
                    view.SetError(task.Exception);
                    return null;
                }
                else
                {
                    var result = task.Result;
                    view.SetCompleted(result.PublicUrl);
                    return result;
                }
            });

            return new UploadViewState(view, finalTask);
        }

        private static async Task<IUploadProvider> GetUploadProvider(SupportedUploadType type)
        {
            var settings = _settings.Uploads;
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
                    dialog.VerificationText = "Set choice as default for " + type;

                    var dialogResult = await dialog.ShowAsNiceDialogAsync(null);

                    if (dialogResult != null && providerLookup.ContainsKey(dialogResult))
                    {
                        var lookup = providerLookup[dialogResult];
                        if (dialog.IsVerificationChecked)
                        {
                            switch (type)
                            {
                                case SupportedUploadType.Image:
                                    settings.Image = lookup;
                                    break;
                                case SupportedUploadType.Video:
                                    settings.Video = lookup;
                                    break;
                                case SupportedUploadType.Text:
                                    settings.Text = lookup;
                                    break;
                                case SupportedUploadType.Binary:
                                    settings.Binary = lookup;
                                    break;
                            }
                        }
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
    }

    public class UploadViewState
    {
        public UploadViewState(ITasksViewItem taskView, Task<UploadResult> uploadResult)
        {
            TaskView = taskView;
            UploadResult = uploadResult;
        }

        public ITasksViewItem TaskView { get; }
        public Task<UploadResult> UploadResult { get; }
    }
}
