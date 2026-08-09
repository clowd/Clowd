using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Clowd.PlatformUtil;
using Clowd.Util;

namespace Clowd
{
    public sealed class SessionWindow
    {
        public string Caption { get; set; }
        public string Class { get; set; }
        public string ImgPath { get; set; }
        public bool Selected { get; set; }
        public int Id { get; set; }
        public ScreenRect Position { get; set; }
    }

    // a record so FileSyncObject.Set's equality check can skip the (synchronous) disk write when
    // a freshly built instance carries the same values as the stored one (ScreenRect is a record).
    public sealed record SessionOpenEditor
    {
        public Guid? VirtualDesktopId { get; set; }
        public bool IsTopMost { get; set; }
        public bool IsMinimized { get; set; }
        public bool IsMaximized { get; set; }
        public ScreenRect RestorePosition { get; set; }
    }

    /// <summary>
    /// The second video track inside a recording's mp4 — the webcam, kept out of the composited
    /// picture on purpose so the overlay can be placed (or dropped) later in the video editor.
    /// Recorded at capture time from obs-express's <c>tracks</c> report, or probed off the file
    /// when the recorder was too old to send one. A record so <see cref="FileSyncObject"/>'s
    /// equality check can skip a redundant disk write, exactly like <see cref="SessionOpenEditor"/>.
    /// </summary>
    public sealed record SessionVideoTrack
    {
        /// <summary>Stream index inside the mp4 (the screen is 0, the webcam 1).</summary>
        public int Index { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class SessionInfo : FileSyncObject
    {
        public SessionInfo(string file) : base(file)
        { }

        protected override JsonTypeInfo GetJsonTypeInfo() => ClowdUiJsonContext.Default.SessionInfo;

        public DateTime CreatedUtc
        {
            get => Get<DateTime>();
            set => Set(value);
        }

        public string PreviewImgPath
        {
            get => Get<string>();
            set
            {
                if (Set(value))
                {
                    OnPropertyChanged(nameof(CanCopy));
                    OnPropertyChanged(nameof(CanUpload));
                }
            }
        }

        public string DesktopImgPath
        {
            get => Get<string>();
            set => Set(value);
        }

        public string CursorImgPath
        {
            get => Get<string>();
            set => Set(value);
        }

        public ScreenRect CursorPosition
        {
            get => Get<ScreenRect>();
            set => Set(value);
        }

        public ScreenRect CroppedRect
        {
            get => Get<ScreenRect>();
            set => Set(value);
        }

        public ScreenRect OriginalBounds
        {
            get => Get<ScreenRect>();
            set => Set(value);
        }

        public SessionOpenEditor OpenEditor
        {
            get => Get<SessionOpenEditor>();
            set => Set(value);
        }

        public SessionWindow[] Windows
        {
            get => Get<SessionWindow[]>();
            set => Set(value);
        }

        public string Name
        {
            get => Get<string>();
            set => Set(value);
        }

        // null/empty for capture/editor sessions; for upload-only sessions (clipboard / file / tray
        // uploads that do not open in the image editor) one of "image", "video", "text", "file".
        // NOTE: "upload-only" is about the *image* editor. A "video" session is IsUploadOnly and
        // still has an editor of its own — the video editor, offered through CanEditVideo.
        public string ContentKind
        {
            get => Get<string>();
            set => Set(value);
        }

        [JsonIgnore] public bool IsUploadOnly => !String.IsNullOrEmpty(ContentKind);

        // set for video ("video") sessions; the playable recording file. Normally lives in the
        // user's configured recording output folder, outside this session's directory (issue #50).
        public string VideoPath
        {
            get => Get<string>();
            set
            {
                if (Set(value))
                {
                    OnPropertyChanged(nameof(CanUpload));
                    OnPropertyChanged(nameof(CanEditVideo));
                    OnPropertyChanged(nameof(ShowEditVideo));
                }
            }
        }

        // recorded duration in milliseconds (from the last obs-express status line).
        public long DurationMs
        {
            get => Get<long>();
            set => Set(value);
        }

        // set on recordings that carry a webcam track; null (the common case) means the mp4 has
        // nothing but the screen. Written once, when the recording session is created — the video
        // editor still derives its own layout from the file it opens, so this exists to decide
        // whether a recording is worth opening the editor for at all, without probing every file.
        public SessionVideoTrack WebcamTrack
        {
            get => Get<SessionVideoTrack>();
            set
            {
                if (Set(value))
                    OnPropertyChanged(nameof(HasWebcamTrack));
            }
        }

        // a track with no dimensions is not one anything can lay out.
        [JsonIgnore]
        public bool HasWebcamTrack => WebcamTrack != null && WebcamTrack.Width > 0 && WebcamTrack.Height > 0;

        [JsonIgnore] public bool IsVideo => String.Equals(ContentKind, "video", StringComparison.OrdinalIgnoreCase);

        // a recording (and a converted GIF) carries a poster frame in PreviewImgPath, but putting
        // that single still on the clipboard is never what the user meant by copying a video, so
        // video entries offer no Copy at all.
        [JsonIgnore] public bool CanCopy => !IsVideo && !String.IsNullOrEmpty(PreviewImgPath);

        // every entry can be (re-)uploaded as long as it isn't busy and owns some content to send.
        // Whether the file is still on disk is settled by UploadSourcePath at the point of use —
        // this one is evaluated by a binding on every row.
        [JsonIgnore]
        public bool CanUpload => ActiveUpload == null
                                 && ActiveGifConversion == null
                                 && ActiveRender == null
                                 && (!String.IsNullOrEmpty(VideoPath) || !String.IsNullOrEmpty(PreviewImgPath) || IsUploadOnly);

        /// <summary>The file an upload of this session sends: the recording itself for a video entry
        /// (not the poster frame PreviewImgPath points at), otherwise the image, otherwise the copy of
        /// the payload an upload-only session keeps beside its session.json. Null when none of them is
        /// on disk any more.</summary>
        [JsonIgnore]
        public string UploadSourcePath
        {
            get
            {
                if (IsVideo && Exists(VideoPath))
                    return VideoPath;

                if (Exists(PreviewImgPath))
                    return PreviewImgPath;

                // a text / file upload-only session has no preview: UploadManager wrote the payload
                // next to session.json as "content.<ext>" when it created the session.
                try
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!String.IsNullOrEmpty(dir))
                        return Directory.EnumerateFiles(dir, "content.*").FirstOrDefault();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // an unreadable session directory simply has nothing to upload.
                }

                return null;
            }
        }

        private static bool Exists(string path)
        {
            return !String.IsNullOrEmpty(path) && File.Exists(path);
        }

        // set only on GIF sessions: the recording this session's gif was converted from. It ties the
        // GIF entry back to its source (so a second conversion finds it instead of starting again)
        // and marks the entry as already-a-gif.
        public string SourceVideoPath
        {
            get => Get<string>();
            set
            {
                if (Set(value))
                {
                    OnPropertyChanged(nameof(CanCreateGif));
                    OnPropertyChanged(nameof(CanEditVideo));
                    OnPropertyChanged(nameof(ShowEditVideo));
                }
            }
        }

        // a GIF can be made from any video recording except one that is itself a GIF.
        [JsonIgnore] public bool CanCreateGif => IsVideo && String.IsNullOrEmpty(SourceVideoPath);

        // set only on "Edited" sessions: the recording this session's video was rendered from. It
        // ties the edited entry back to its source, so re-rendering the same recording replaces
        // that entry instead of piling up a new one. Deliberately NOT SourceVideoPath, which means
        // "this session is a GIF" and switches CanCreateGif off.
        public string EditSourceVideoPath
        {
            get => Get<string>();
            set => Set(value);
        }

        // any recording can be opened in the video editor, except a GIF (nothing to trim, no audio,
        // and the render tool would not read it back as a video).
        [JsonIgnore]
        public bool CanEditVideo => IsVideo && String.IsNullOrEmpty(SourceVideoPath) && !String.IsNullOrEmpty(VideoPath);

        // what the Recent page binds to: the video editor is Windows-only for now (the render tool
        // and the webcam track only ship there), so the affordance is hidden rather than offered
        // and then refused.
        [JsonIgnore]
        public bool ShowEditVideo => CanEditVideo && OperatingSystem.IsWindows();

        public string UploadFileKey
        {
            get => Get<string>();
            set => Set(value);
        }

        public string UploadUrl
        {
            get => Get<string>();
            set
            {
                if (Set(value))
                {
                    OnPropertyChanged(nameof(AllUploads));
                    OnPropertyChanged(nameof(ShowNotUploaded));
                }
            }
        }

        public UploadRecord[] Uploads
        {
            get => Get<UploadRecord[]>();
            set
            {
                if (Set(value))
                {
                    OnPropertyChanged(nameof(AllUploads));
                    OnPropertyChanged(nameof(ShowNotUploaded));
                }
            }
        }

        // not persisted — the in-flight upload for this session, set by UploadsManager.
        [JsonIgnore]
        public Clowd.UI.ActiveUpload ActiveUpload
        {
            get => _activeUpload;
            set
            {
                _activeUpload = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowNotUploaded));
                OnPropertyChanged(nameof(CanUpload));
            }
        }

        // not persisted — the in-flight video→gif conversion filling this session in, set by
        // GifConversionManager. Non-null only while it runs.
        [JsonIgnore]
        public Clowd.UI.Services.GifConversion ActiveGifConversion
        {
            get => _activeGifConversion;
            set
            {
                _activeGifConversion = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowNotUploaded));
                OnPropertyChanged(nameof(CanUpload));
                OnPropertyChanged(nameof(IsIdle));
            }
        }

        // not persisted — the in-flight video render filling this session in, set by
        // VideoRenderManager. Non-null only while it runs; the mirror image of
        // ActiveGifConversion, and treated the same everywhere a busy entry is.
        [JsonIgnore]
        public Clowd.UI.Services.VideoRender ActiveRender
        {
            get => _activeRender;
            set
            {
                _activeRender = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowNotUploaded));
                OnPropertyChanged(nameof(CanUpload));
                OnPropertyChanged(nameof(IsIdle));
            }
        }

        // false while a child process is still writing this entry's file: there is nothing to
        // play, open, upload or reveal until it lands. Bound by the row's action bar and its
        // context menu.
        [JsonIgnore] public bool IsIdle => ActiveGifConversion == null && ActiveRender == null;

        // the full upload history: the persisted list when present, otherwise a single synthesized
        // record from the legacy UploadUrl/UploadFileKey fields (Provider null → not deletable).
        [JsonIgnore]
        public UploadRecord[] AllUploads
        {
            get
            {
                var uploads = Uploads;
                if (uploads != null && uploads.Length > 0)
                    return uploads;

                if (!String.IsNullOrEmpty(UploadUrl))
                    return new[] { new UploadRecord { Url = UploadUrl, UploadKey = UploadFileKey } };

                return Array.Empty<UploadRecord>();
            }
        }

        // a session still being written into has nothing to upload yet, so it says nothing rather
        // than "Not uploaded" over the top of its own progress row.
        [JsonIgnore]
        public bool ShowNotUploaded => ActiveGifConversion == null && ActiveRender == null && ActiveUpload == null && AllUploads.Length == 0;

        private Clowd.UI.ActiveUpload _activeUpload;
        private Clowd.UI.Services.GifConversion _activeGifConversion;
        private Clowd.UI.Services.VideoRender _activeRender;
    }
}
