using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI;
using Clowd.UI.Dialogs;
using Clowd.UI.Helpers;
using Clowd.Upload;

namespace Clowd
{
    // port of the WPF UploadManager: uploads go to the default provider for the content type,
    // falling back to a provider-selection dialog when no default is set. DotNetZip was replaced
    // with System.IO.Compression; the toast-based progress UI remains a no-op TasksView for now,
    // so completion feedback is a clipboard copy + notice dialog instead.
    public static class UploadManager
    {
        private static ITasksView _view => PageManager.Current.Tasks;
        private static readonly IMimeProvider _mime = new MimeProvider();

        public static async Task<UploadResult> UploadSession(SessionInfo session, IUploadProvider provider = null)
        {
            provider ??= await GetUploadProvider(SupportedUploadType.Image);
            if (provider == null)
                return null;

            var view = _view.CreateTask(session.Name);
            view.SetStatus("Uploading...");
            view.Show();

            var info = new FileInfo(session.PreviewImgPath);

            UploadProgressHandler handler = (bytesUploaded) =>
            {
                view.SetProgress(bytesUploaded, info.Length, true);
                Dispatcher.UIThread.Post(() =>
                {
                    session.UploadProgress = bytesUploaded / (double)info.Length * 100d;
                });
            };

            var fileName = GetPatternFileName(Path.GetExtension(info.Name));
            var uploadTask = provider.UploadAsync(info.FullName, handler, fileName, view.CancelToken);
            var result = await HandleUploadResult(view, uploadTask);

            if (result != null)
            {
                session.UploadUrl = result.PublicUrl;
                session.UploadFileKey = result.UploadKey;
            }

            return result;
        }

        public static async Task<UploadResult> UploadImage(Avalonia.Media.Imaging.Bitmap image, string imgDisplayName)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Image);
            if (provider == null)
                return null;

            using var ms = new MemoryStream();
            image.Save(ms);
            ms.Position = 0;
            var fileName = GetPatternFileName(".png");

            var view = _view.CreateTask(imgDisplayName);
            view.SetStatus("Uploading...");
            view.Show();

            UploadProgressHandler handler = (bytesUploaded) => view.SetProgress(bytesUploaded, ms.Length, true);
            var uploadTask = provider.UploadAsync(ms, handler, fileName, view.CancelToken);
            return await HandleUploadResult(view, uploadTask);
        }

        public static async Task<UploadResult> UploadText(string text, string textType)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Text);
            if (provider == null)
                return null;

            var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));

            var view = _view.CreateTask(textType);
            view.SetStatus("Uploading...");
            view.Show();

            UploadProgressHandler handler = (bytesUploaded) => view.SetProgress(bytesUploaded, ms.Length, true);

            var fileName = GetRandomName(10);
            var uploadTask = provider.UploadAsync(ms, handler, fileName, view.CancelToken);
            return await HandleUploadResult(view, uploadTask);
        }

        public static async Task<UploadResult> UploadFile(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var fileName = Path.GetFileName(filePath);
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

            UploadProgressHandler handler = (bytesUploaded) => view.SetProgress(bytesUploaded, fileInfo.Length, true);
            var uploadTask = provider.UploadAsync(filePath, handler, fileName, view.CancelToken);
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

        public static IEnumerable<IUploadProvider> GetAvailableProviders(SupportedUploadType type)
        {
            var settings = SettingsRoot.Current.Uploads;
            var defaultProvider = settings.GetDefaultProvider(type)?.Provider;

            // callers (e.g. the editor's right-click upload menu) rely on the default coming first
            return settings.GetEnabledProviders(type)
                .Select(p => p.Provider)
                .OrderByDescending(p => ReferenceEquals(p, defaultProvider))
                .ToArray();
        }

        private static async Task<UploadResult> ZipUpload(string[] filePaths)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Binary);
            if (provider == null)
                return null;

            var tmpFolder = Directory.CreateTempSubdirectory("clowd-zip").FullName;
            var zipPath = Path.Combine(tmpFolder, GetRandomName(8) + ".zip");

            try
            {
                var view = _view.CreateTask("Archive");
                view.SetStatus("Compressing...");
                view.Show();

                var anyAdded = await Task.Run(() =>
                {
                    using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                    var added = false;
                    foreach (var path in filePaths)
                    {
                        view.CancelToken.ThrowIfCancellationRequested();

                        if (Directory.Exists(path))
                        {
                            var root = Path.GetFullPath(path);
                            var rootName = Path.GetFileName(root);
                            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                            {
                                view.CancelToken.ThrowIfCancellationRequested();
                                var entryName = Path.Combine(rootName, Path.GetRelativePath(root, file)).Replace('\\', '/');
                                zip.CreateEntryFromFile(file, entryName);
                                added = true;
                            }
                        }
                        else if (File.Exists(path))
                        {
                            zip.CreateEntryFromFile(path, Path.GetFileName(path));
                            added = true;
                        }
                    }

                    return added;
                }, view.CancelToken);

                // no files were added to the archive; there is nothing to upload
                if (!anyAdded)
                {
                    view.Hide();
                    return null;
                }

                var size = new FileInfo(zipPath).Length;

                view.SetStatus("Uploading...");
                view.SetProgress(0, size, true);

                UploadProgressHandler handler = (bytesUploaded) => view.SetProgress(bytesUploaded, size, true);

                var archiveName = GetRandomName(10) + ".zip";
                var uploadTask = provider.UploadAsync(zipPath, handler, archiveName, view.CancelToken);
                return await HandleUploadResult(view, uploadTask);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                try { Directory.Delete(tmpFolder, true); } catch {; }
            }
        }

        private static async Task<UploadResult> HandleUploadResult(ITasksViewItem view, Task<UploadResult> uploadTask)
        {
            UploadResult result;

            try
            {
                result = await uploadTask;
            }
            catch (OperationCanceledException)
            {
                view.Hide();
                return null;
            }
            catch (Exception ex)
            {
                // surfaced in the TaskWindow overlay (error state + dismiss)
                view.SetError(ex);
                return null;
            }

            // the overlay swaps the progress bar for a "Copy to Clipboard" action
            view.SetCompleted(result.PublicUrl);
            return result;
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
                if (await NiceDialog.ShowYesNoPromptAsync(null, NiceDialogIcon.Information,
                        $"There is no upload provider configured/enabled for '{type}'. Would you like to open settings and configure one now?",
                        "No upload provider available"))
                {
                    PageManager.Current.GetSettingsPage().Open(SettingsPageTab.SettingsUploads);
                }

                return null;
            }

            var selection = await ProviderSelectionDialog.ShowAsync(type, enabled);
            if (selection == null)
                return null;

            if (selection.SetAsDefault)
                settings.SetDefaultProvider(selection.Info, type);

            return selection.Info.Provider;
        }

        private static string GetPatternFileName(string extension)
        {
            var filePattern = SettingsRoot.Current.Capture.FilenamePattern;
            filePattern ??= "yyyy-MM-dd HH-mm-ss";
            filePattern = Path.GetFileNameWithoutExtension(filePattern);
            return DateTime.Now.ToString(filePattern) + extension;
        }

        private static string GetRandomName(int length)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            return new string(chars);
        }
    }
}
