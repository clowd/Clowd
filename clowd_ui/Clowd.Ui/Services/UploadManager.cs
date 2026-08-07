using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Clowd.Config;
using Clowd.UI;
using Clowd.UI.Dialogs;
using Clowd.UI.Helpers;
using Clowd.Upload;

namespace Clowd
{
    // port of the WPF UploadManager: uploads go to the default provider for the content type,
    // falling back to a provider-selection dialog when no default is set. DotNetZip was replaced
    // with System.IO.Compression. Progress, results, and errors surface on the Recent page via
    // UploadsManager (the old tray-adjacent TaskWindow overlay was removed).
    public static class UploadManager
    {
        private static UploadsManager _uploads => PageManager.Current.Uploads;
        private static readonly IMimeProvider _mime = new MimeProvider();

        public static async Task<UploadResult> UploadSession(SessionInfo session, IUploadProvider provider = null)
        {
            // video sessions upload the recording itself (via the Video provider), not the poster
            // frame that PreviewImgPath points at (see the video-recording design, §4.4); an
            // upload-only session with no image sends the payload copy it kept. All three cases are
            // resolved by UploadSourcePath, which also tells us whether the file is still there.
            var path = session.UploadSourcePath;
            if (String.IsNullOrEmpty(path))
            {
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                    "The file this item was made from could not be found. It may have been moved or deleted.",
                    "Nothing to upload");
                return null;
            }

            var isVideo = String.Equals(path, session.VideoPath, StringComparison.OrdinalIgnoreCase);

            provider ??= await GetUploadProvider(isVideo
                ? SupportedUploadType.Video
                : GetSupportedType(Path.GetExtension(path)));
            if (provider == null)
                return null;

            var upload = _uploads.StartUpload(session.Name, session);
            if (upload == null)
                return null; // an upload is already in flight for this session
            upload.SetStatus("Uploading...");

            var info = new FileInfo(path);

            UploadProgressHandler handler = (bytesUploaded) => upload.SetProgress(bytesUploaded, info.Length, true);

            var fileName = GetPatternFileName(Path.GetExtension(info.Name));
            var uploadTask = Upload(provider, info.FullName, handler, upload, fileName);

            // CompleteUpload persists UploadUrl/UploadFileKey (and the new Uploads list) on the session.
            return await HandleUploadResult(upload, uploadTask);
        }

        public static async Task<UploadResult> UploadImage(Avalonia.Media.Imaging.Bitmap image, string imgDisplayName)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Image);
            if (provider == null)
                return null;

            var session = SessionManager.Current.CreateNewSession();
            session.Name = imgDisplayName;
            session.ContentKind = "image";

            using var ms = new MemoryStream();
            image.Save(ms, PngBitmapEncoderOptions.Default);

            // persist the image into the session dir so the recents list shows a preview.
            try
            {
                var previewPath = Path.Combine(Path.GetDirectoryName(session.FilePath), "content.png");
                File.WriteAllBytes(previewPath, ms.ToArray());
                session.PreviewImgPath = previewPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("failed to write session preview: " + ex);
                SentryConfig.CaptureHandled(ex, "upload.write-preview");
            }

            ms.Position = 0;
            var fileName = GetPatternFileName(".png");

            var upload = _uploads.StartUpload(imgDisplayName, session);
            if (upload == null)
                return null;
            upload.SetStatus("Uploading...");

            UploadProgressHandler handler = (bytesUploaded) => upload.SetProgress(bytesUploaded, ms.Length, true);
            var uploadTask = Upload(provider, ms, handler, upload, fileName);
            return await HandleUploadResult(upload, uploadTask);
        }

        public static async Task<UploadResult> UploadText(string text, string textType)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Text);
            if (provider == null)
                return null;

            var session = SessionManager.Current.CreateNewSession();
            session.Name = textType;
            session.ContentKind = "text";

            // keep a copy of the uploaded text in the session dir for reference.
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(session.FilePath), "content.txt"), text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("failed to write session text: " + ex);
                SentryConfig.CaptureHandled(ex, "upload.write-text");
            }

            var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));

            var upload = _uploads.StartUpload(textType, session);
            if (upload == null)
                return null;
            upload.SetStatus("Uploading...");

            UploadProgressHandler handler = (bytesUploaded) => upload.SetProgress(bytesUploaded, ms.Length, true);

            var fileName = GetRandomName(10);
            var uploadTask = Upload(provider, ms, handler, upload, fileName);
            return await HandleUploadResult(upload, uploadTask);
        }

        public static async Task<UploadResult> UploadFile(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var fileName = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath);
            var category = _mime.GetCategoryFromExtension(extension);

            var stype = GetSupportedType(extension);

            var provider = await GetUploadProvider(stype);
            if (provider == null)
                return null;

            var session = SessionManager.Current.CreateNewSession();
            session.Name = "File Upload";
            session.ContentKind = category switch
            {
                ContentCategory.Image => "image",
                ContentCategory.Video => "video",
                ContentCategory.Text => "text",
                _ => "file",
            };

            // for images, copy the file into the session dir so the recents list shows a preview.
            if (category == ContentCategory.Image)
            {
                try
                {
                    var dest = Path.Combine(Path.GetDirectoryName(session.FilePath), "content" + extension);
                    File.Copy(filePath, dest, true);
                    session.PreviewImgPath = dest;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("failed to copy session preview: " + ex);
                    SentryConfig.CaptureHandled(ex, "upload.copy-preview");
                }
            }

            var upload = _uploads.StartUpload($"{stype} ({fileName})", session);
            if (upload == null)
                return null;
            upload.SetStatus("Uploading...");

            UploadProgressHandler handler = (bytesUploaded) => upload.SetProgress(bytesUploaded, fileInfo.Length, true);
            var uploadTask = Upload(provider, filePath, handler, upload, fileName);
            return await HandleUploadResult(upload, uploadTask);
        }

        public static async Task<UploadResult> UploadSeveralFiles(params string[] filePaths)
        {
            // the zip-or-direct decision (including dangerous-file wrapping) lives in
            // UploadRouting so it is unit testable away from the UI.
            var decision = UploadRouting.ShouldZip(filePaths, SettingsRoot.Current.Uploads.WrapDangerousUploadsInZip,
                _mime, File.Exists, p => new FileInfo(p).Length);

            if (!decision.Zip)
                return await UploadFile(Path.GetFullPath(filePaths[0]));

            return await ZipUpload(filePaths, decision.ArchiveName);
        }

        /// <summary>Prompts the user to pick an upload destination for the given content type
        /// (optionally saving it as the new default), even when a default is already set.</summary>
        public static async Task<IUploadProvider> SelectProvider(SupportedUploadType type)
        {
            var settings = SettingsRoot.Current.Uploads;
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

        private static async Task<UploadResult> ZipUpload(string[] filePaths, string archiveName = null)
        {
            var provider = await GetUploadProvider(SupportedUploadType.Binary);
            if (provider == null)
                return null;

            var session = SessionManager.Current.CreateNewSession();
            session.Name = "File Upload";
            session.ContentKind = "file";

            // providers that accept a non-seekable stream get the zip piped straight into the
            // upload; the rest spool it to a temp file first (below). Accelerated configs stream
            // too: the protocol takes unknown-length sources, so the zip pipes straight through
            // and the early share link still fires the moment the session is created.
            if (provider is UploadProviderBase { SupportsUnseekableUpload: true } streamable)
                return await StreamingZipUpload(streamable, filePaths, session, archiveName);

            var tmpFolder = Directory.CreateTempSubdirectory("clowd-zip").FullName;
            var zipPath = Path.Combine(tmpFolder, GetRandomName(8) + ".zip");

            ActiveUpload upload = null;
            try
            {
                upload = _uploads.StartUpload("Archive", session);
                if (upload == null)
                {
                    // no in-flight upload is possible on a fresh session; bail and clean up.
                    TryDeleteSession(session);
                    return null;
                }
                upload.SetStatus("Compressing...");

                var anyAdded = await Task.Run(() =>
                {
                    using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                    var added = false;
                    foreach (var path in filePaths)
                    {
                        upload.CancelToken.ThrowIfCancellationRequested();

                        if (Directory.Exists(path))
                        {
                            var root = Path.GetFullPath(path);
                            var rootName = Path.GetFileName(root);
                            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                            {
                                upload.CancelToken.ThrowIfCancellationRequested();
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
                }, upload.CancelToken);

                // no files were added to the archive; there is nothing to upload
                if (!anyAdded)
                {
                    _uploads.DiscardUpload(upload);
                    return null;
                }

                var size = new FileInfo(zipPath).Length;

                upload.SetStatus("Uploading...");
                upload.SetProgress(0, size, true);

                UploadProgressHandler handler = (bytesUploaded) => upload.SetProgress(bytesUploaded, size, true);

                archiveName ??= GetRandomName(10) + ".zip";
                var uploadTask = Upload(provider, zipPath, handler, upload, archiveName);
                return await HandleUploadResult(upload, uploadTask);
            }
            catch (OperationCanceledException)
            {
                // cancellation raised during compression leaks the row unless discarded here.
                if (upload != null)
                    _uploads.DiscardUpload(upload);
                return null;
            }
            finally
            {
                try { Directory.Delete(tmpFolder, true); } catch {; }
            }
        }

        private static async Task<UploadResult> StreamingZipUpload(UploadProviderBase provider, string[] filePaths, SessionInfo session,
            string archiveName)
        {
            ActiveUpload upload = null;
            try
            {
                upload = _uploads.StartUpload("Archive", session);
                if (upload == null)
                {
                    // no in-flight upload is possible on a fresh session; bail and clean up.
                    TryDeleteSession(session);
                    return null;
                }

                var composer = await Task.Run(() => ZipStreamComposer.Create(filePaths), upload.CancelToken);

                // no files behind the given paths; there is nothing to upload
                if (!composer.HasEntries)
                {
                    _uploads.DiscardUpload(upload);
                    return null;
                }

                // progress is the source bytes consumed by the compressor — the compressed size
                // isn't known up front, but the input size is.
                upload.SetStatus("Uploading...");
                upload.SetProgress(0, composer.TotalSourceBytes, true);

                archiveName ??= GetRandomName(10) + ".zip";
                return await HandleUploadResult(upload, RunStreamingZip(provider, composer, upload, archiveName));
            }
            catch (OperationCanceledException)
            {
                if (upload != null)
                    _uploads.DiscardUpload(upload);
                return null;
            }
            catch (Exception ex)
            {
                // enumerating the selection can fail outright (an ACL-protected folder, a file
                // that vanished): report it the same way a failed transfer is reported, or the
                // row would sit there forever with the exception unobserved.
                if (upload != null)
                    await _uploads.FailUpload(upload, ex);
                else
                    TryDeleteSession(session);

                SentryConfig.CaptureHandled(ex, "upload.zip-stream");
                return null;
            }
        }

        private static async Task<UploadResult> RunStreamingZip(UploadProviderBase provider, ZipStreamComposer composer, ActiveUpload upload,
            string archiveName)
        {
            // the producer writes the zip into the pipe while the provider consumes it; ~8 MiB of
            // backpressure keeps memory bounded when the network is slower than the disk.
            var pipe = new Pipe(new PipeOptions(
                pauseWriterThreshold: 8 * 1024 * 1024, resumeWriterThreshold: 4 * 1024 * 1024, useSynchronizationContext: false));

            // The composer counts source bytes it has read, which runs ahead of the wire by the
            // pipe plus the provider's staging buffer — on a small archive that reads as a row
            // stuck at 100% for the entire real transfer. There is no wire-accurate alternative
            // (the compressed total isn't known until the last byte), so hold the bar just short
            // of full and let the finished upload below fill it in.
            void ReportSourceProgress(long consumed, long total)
                => upload.SetProgress(Math.Min(consumed, (long)(total * 0.95)), total, true);

            using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(upload.CancelToken);
            var producer = Task.Run(async () =>
            {
                try
                {
                    var dest = pipe.Writer.AsStream(leaveOpen: true);
                    await composer.WriteAsync(dest, ReportSourceProgress, producerCts.Token);
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception ex)
                {
                    // faulting the writer resurfaces this exception from the consumer's reads,
                    // so a compression failure aborts the transfer with the original error.
                    await pipe.Writer.CompleteAsync(ex);
                    throw;
                }
            });

            try
            {
                // provider progress callbacks are discarded — the producer drives the progress bar
                var result = await Upload(provider, pipe.Reader.AsStream(), _ => { }, upload, archiveName);
                await producer;
                upload.SetProgress(composer.TotalSourceBytes, composer.TotalSourceBytes, true);
                return result;
            }
            catch
            {
                // the consumer failed (or was cancelled) first — stop and drain the producer so
                // the exception that reaches HandleUploadResult is the one that struck first.
                producerCts.Cancel();
                await pipe.Reader.CompleteAsync();
                try { await producer; } catch {; }
                throw;
            }
        }

        // Routes through the early-URL-aware overload when the provider supports it (all shipped
        // providers derive from UploadProviderBase), so an accelerated upload can surface its
        // shareable link the instant the server session is created.
        private static Task<UploadResult> Upload(IUploadProvider provider, string filePath, UploadProgressHandler handler, ActiveUpload upload,
            string fileName)
            => provider is UploadProviderBase b
                ? b.UploadAsync(filePath, handler, url => _uploads.SetEarlyUrl(upload, url), fileName, upload.CancelToken)
                : provider.UploadAsync(filePath, handler, fileName, upload.CancelToken);

        private static Task<UploadResult> Upload(IUploadProvider provider, Stream stream, UploadProgressHandler handler, ActiveUpload upload,
            string fileName)
            => provider is UploadProviderBase b
                ? b.UploadAsync(stream, handler, url => _uploads.SetEarlyUrl(upload, url), fileName, upload.CancelToken)
                : provider.UploadAsync(stream, handler, fileName, upload.CancelToken);

        private static async Task<UploadResult> HandleUploadResult(ActiveUpload upload, Task<UploadResult> uploadTask)
        {
            UploadResult result;

            try
            {
                result = await uploadTask;
            }
            catch (OperationCanceledException)
            {
                _uploads.DiscardUpload(upload);
                return null;
            }
            catch (Exception ex)
            {
                await _uploads.FailUpload(upload, ex);
                SentryConfig.CaptureHandled(ex, "upload.transfer");
                return null;
            }

            _uploads.CompleteUpload(upload, result);
            return result;
        }

        /// <summary>Resolves the provider an upload was made with; null if it is no longer installed.</summary>
        private static IUploadProvider FindProvider(string providerTypeName) =>
            String.IsNullOrEmpty(providerTypeName) ? null :
            SettingsRoot.Current?.Uploads?.Providers
                        .Select(p => p.Provider)
                        .FirstOrDefault(p => p.GetType().Name == providerTypeName);

        private static UploadDeleteInfo ToDeleteInfo(UploadRecord record) => new UploadDeleteInfo
        {
            UploadKey = record.UploadKey,
            DeleteKey = record.DeleteKey,
            FileName = record.FileName,
            PublicUrl = record.Url,
        };

        public static bool CanDeleteUpload(UploadRecord record)
        {
            var provider = FindProvider(record?.Provider);
            return provider != null && provider.CanDelete(ToDeleteInfo(record));
        }

        public static Task DeleteUploadAsync(UploadRecord record)
        {
            var provider = FindProvider(record?.Provider);
            if (provider == null)
                throw new InvalidOperationException("The upload provider for this file is no longer available.");

            return provider.DeleteAsync(ToDeleteInfo(record), CancellationToken.None);
        }

        /// <summary>Which kind of upload destination a file's extension calls for.</summary>
        private static SupportedUploadType GetSupportedType(string extension)
        {
            return _mime.GetCategoryFromExtension(extension) switch
            {
                ContentCategory.Image => SupportedUploadType.Image,
                ContentCategory.Text => SupportedUploadType.Text,
                ContentCategory.Video => SupportedUploadType.Video,
                _ => SupportedUploadType.Binary,
            };
        }

        private static async Task<IUploadProvider> GetUploadProvider(SupportedUploadType type)
        {
            UploadProviderInfo provider = SettingsRoot.Current.Uploads.GetDefaultProvider(type);

            if (provider != null)
                return provider.Provider;

            return await SelectProvider(type);
        }

        private static void TryDeleteSession(SessionInfo session)
        {
            try { SessionManager.Current.DeleteSession(session); }
            catch (Exception ex)
            {
                Debug.WriteLine("failed to delete session: " + ex);
                SentryConfig.CaptureHandled(ex, "upload.delete-session");
            }
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
