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

        // corner radius (in pixels of CroppedRect) the capture's corners are rounded with — the OS
        // corner radius of the window the user picked in the capturer. 0 / absent = square, which
        // is every dragged selection, every scrolling capture and every session written before the
        // key existed (CAPTURE_PROTOCOL.md §1.3). The editor seeds its image graphic's CornerRadius
        // from it; after that the graphic's own (user-editable) value is the one that matters.
        public double CornerRadius
        {
            get => Get<double>();
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
        // uploads that have no editor) one of "image", "video", "text", "file".
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
                    OnPropertyChanged(nameof(CanUpload));
            }
        }

        // recorded duration in milliseconds (from the last obs-express status line).
        public long DurationMs
        {
            get => Get<long>();
            set => Set(value);
        }

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
                    OnPropertyChanged(nameof(CanCreateGif));
            }
        }

        // a GIF can be made from any video recording except one that is itself a GIF.
        [JsonIgnore] public bool CanCreateGif => IsVideo && String.IsNullOrEmpty(SourceVideoPath);

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
            }
        }

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
        public bool ShowNotUploaded => ActiveGifConversion == null && ActiveUpload == null && AllUploads.Length == 0;

        private Clowd.UI.ActiveUpload _activeUpload;
        private Clowd.UI.Services.GifConversion _activeGifConversion;
    }
}
