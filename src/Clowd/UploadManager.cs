using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using Clowd.Config;
using Clowd.UI.Helpers;
using Clowd.UI.Pages;
using Clowd.Upload;
using Clowd.Util;
using Ionic.Zip;
using RT.Util.ExtensionMethods;

namespace Clowd
{
    public static class UploadManager
    {
        private static readonly ITasksView _view = new TasksViewManager();
        private static readonly IMimeProvider _mime = new MimeProvider();

        public static async Task<UploadResult> UploadSession(SessionInfo session)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Image);
            if (provider == null)
                return null;

            var view = _view.CreateTask(session.Name);
            view.SetStatus("Uploading...");
            view.Show();

            var info = new FileInfo(session.PreviewImgPath);

            UploadProgressHandler handler = (bytesUploaded) =>
            {
                view.SetProgress(bytesUploaded, info.Length, true);
                App.Current.Dispatcher.Invoke(() =>
                {
                    session.UploadProgress = bytesUploaded / (double)info.Length * 100d;
                });
            };

            var fileName = RandomEx.GetCryptoUniqueString(10) + ".png";
            var uploadTask = provider.UploadAsync(info.FullName, handler, fileName, view.CancelToken);
            var result = await HandleUploadResult(view, uploadTask);

            if (result != null)
            {
                session.UploadUrl = result.PublicUrl;
                session.UploadFileKey = result.UploadKey;
            }

            return result;
        }
        
        public static async Task<UploadResult> UploadImage(BitmapSource image, string imgType)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Image);
            if (provider == null)
                return null;
        
            var ms = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            enc.Save(ms);
        
            ms.Position = 0;
        
            var view = _view.CreateTask(imgType);
            view.SetStatus("Uploading...");
            view.Show();
        
            UploadProgressHandler handler = (bytesUploaded) => view.SetProgress(bytesUploaded, ms.Length, true);
        
            var fileName = RandomEx.GetCryptoUniqueString(10) + ".png";
            var uploadTask = provider.UploadAsync(ms, handler, fileName, view.CancelToken);
            return await HandleUploadResult(view, uploadTask);
        }

        public static async Task<UploadResult> UploadText(string text, string textType)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Text);
            if (provider == null)
                return null;

            var ms = new MemoryStream(text.ToUtf8());

            var view = _view.CreateTask(textType);
            view.SetStatus("Uploading...");
            view.Show();

            UploadProgressHandler handler = (bytesUploaded) => view.SetProgress(bytesUploaded, ms.Length, true);

            var fileName = RandomEx.GetCryptoUniqueString(10);
            var uploadTask = provider.UploadAsync(ms, handler, fileName, view.CancelToken);
            return await HandleUploadResult(view, uploadTask);
        }

        public static async Task<UploadResult> UploadFile(string filePath, string fileNameOverride = null)
        {
            var fileInfo = new FileInfo(filePath);
            var fileName = fileNameOverride ?? Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath);
            var category = _mime.GetCategoryFromExtension(extension);

            var stype = category switch
            {
                ContentCategory.Image => SupportedUploadType.Image,
                ContentCategory.Text => SupportedUploadType.Text,
                ContentCategory.Video => SupportedUploadType.Video,
                _ => SupportedUploadType.Binary,
            };

            var provider = await GetUploadProvider(stype);
            if (provider == null)
                return null;

            var view = _view.CreateTask($"{stype} ({fileName})");
            view.SetStatus("Uploading...");
            view.Show();

            var uniqueName = RandomEx.GetCryptoUniqueString(10) + "_" + fileName;
            UploadProgressHandler handler = (bytesUploaded) => view.SetProgress(bytesUploaded, fileInfo.Length, true);

            var uploadTask = provider.UploadAsync(filePath, handler, uniqueName, view.CancelToken);
            return await HandleUploadResult(view, uploadTask);
        }

        public static async Task<UploadResult> UploadSeveralFiles(params string[] filePaths)
        {
            if (filePaths.Length == 1 && File.Exists(filePaths[0]))
            {
                var path = Path.GetFullPath(filePaths[0]);
                var info = new FileInfo(path);
                var ext = Path.GetExtension(path);
                var mime = _mime.GetMimeFromExtension(ext);
                var category = _mime.GetCategoryFromExtension(ext);

                // zip the single file if:
                // - the file type is unknown / is not a special type like image (can not be rendered nicely in browser)
                // - we think the mime type might be compressible
                // - the file size is > 5mb
                var compress = category == ContentCategory.Unknown && mime.Compressible != false && info.Length > 1024 * 1024 * 5;
                if (!compress)
                {
                    return await UploadFile(path);
                }
            }

            return await ZipUpload(filePaths);
        }

        private static async Task<UploadResult> ZipUpload(string[] filePaths)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Binary);
            if (provider == null)
                return null;

            using var tmpFolder = PathEx.GetTempFolder();
            var zipPath = tmpFolder.GetTempFilePath(".zip");

            using ZipFile zip = new ZipFile();
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

            var view = _view.CreateTask("Archive");
            view.SetStatus("Compressing...");
            view.Show();

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
                        view.SetProgress(e.EntriesSaved, e.EntriesTotal, false);
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
            view.SetProgress(0, size, true);

            UploadProgressHandler handler = (bytesUploaded) => view.SetProgress(bytesUploaded, size, true);

            var archiveName = RandomEx.GetCryptoUniqueString(10) + ".zip";
            var uploadTask = provider.UploadAsync(zipPath, handler, archiveName, view.CancelToken);
            return await HandleUploadResult(view, uploadTask);
        }

        private static Task<UploadResult> HandleUploadResult(ITasksViewItem view, Task<UploadResult> uploadTask)
        {
            return uploadTask.ContinueWith(task =>
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
        }

        private static async Task<IUploadProvider> GetUploadProvider(SupportedUploadType type)
        {
            var settings = SettingsRoot.Current.Uploads;
            UploadProviderInfo provider = settings.GetDefaultProvider(type);

            if (provider != null)
                return provider.Provider;

            var enabled = settings.GetEnabledProviders(type).ToArray();
            if (enabled.Length == 0)
            {
                await NiceDialog.ShowSettingsPromptAsync(null, SettingsPageTab.SettingsUploads,
                    $"There is no upload provider configured/enabled for '{type}'. Please visit settings to configure before uploading.");

                return null;
            }
            
            var dialog = new TaskDialogPage()
            {
                Caption = $"{type} Upload",
                Heading = "Select an upload destination:",
                Text = $"You have not selected a default upload provider for '{type}', where would you like to send your file?",
                Verification = $"Set choice as default for {type}",
            };

            Dictionary<TaskDialogButton, UploadProviderInfo> providerLookup = new Dictionary<TaskDialogButton, UploadProviderInfo>();

            foreach (var p in enabled)
            {
                var btn = new TaskDialogCommandLinkButton()
                {
                    Text = p.Provider.Name,
                    DescriptionText = p.Provider.Description,
                };
                dialog.Buttons.Add(btn);
                providerLookup[btn] = p;
            }

            var dialogResult = await dialog.ShowAsNiceDialogAsync(null);

            if (dialogResult != null && providerLookup.ContainsKey(dialogResult))
            {
                var lookup = providerLookup[dialogResult];
                if (dialog.Verification?.Checked == true)
                {
                    settings.SetDefaultProvider(lookup, type);
                }

                return lookup.Provider;
            }

            return null;
        }
    }
}
