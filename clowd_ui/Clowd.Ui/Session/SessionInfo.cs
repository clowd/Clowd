using System;
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
            set => Set(value);
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
            set => Set(value);
        }

        // recorded duration in milliseconds (from the last obs-express status line).
        public long DurationMs
        {
            get => Get<long>();
            set => Set(value);
        }

        [JsonIgnore] public bool IsVideo => String.Equals(ContentKind, "video", StringComparison.OrdinalIgnoreCase);

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

        [JsonIgnore] public bool ShowNotUploaded => ActiveUpload == null && AllUploads.Length == 0;

        private Clowd.UI.ActiveUpload _activeUpload;
    }
}
