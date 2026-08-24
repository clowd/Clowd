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

    /// <summary>
    /// One audio track inside a recording's mp4, as the recorder reported it. The file says how many
    /// audio streams it carries but never what they are, so this is the only place the editor can
    /// learn that stream 2 is the microphone and stream 3 the system mix — it names the rows, and
    /// nothing more. Absent (or empty) on recordings made before separate audio tracks existed, or
    /// by a recorder too old to report them.
    /// </summary>
    public sealed record SessionAudioTrack
    {
        /// <summary>Index among the mp4's audio streams (0 = the first audio stream).</summary>
        public int Index { get; set; }

        /// <summary>The recorder's own word for what fed the track: "speaker", "microphone", or
        /// "mixed" (one track carrying every device). Null when it did not say.</summary>
        public string Kind { get; set; }
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
                    OnPropertyChanged(nameof(CanShowInFolder));
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

        // corner radius the capture's corners are rounded with — the OS corner radius of the
        // window the user picked in the capturer, in pixels of CroppedRect for a screenshot and of
        // OriginalBounds (the recording region) for a recording. 0 / absent = square, which is
        // every dragged selection, every scrolling capture and every session written before the
        // key existed (CAPTURE_PROTOCOL.md §1.3). The image editor seeds its image graphic's
        // CornerRadius from it and the video editor its screen track's rounded-rect mask; after
        // that the editor's own (user-editable) value is the one that matters.
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
        // uploads that do not open in the image editor) one of "image", "video", "text", "file".
        // NOTE: "upload-only" is about the *image* editor. A "video" session is IsUploadOnly and
        // still has an editor of its own — the video editor, offered through CanEditVideo.
        public string ContentKind
        {
            get => Get<string>();
            set
            {
                if (Set(value))
                    RaiseRowAffordances();
            }
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
                    RaiseRowAffordances();
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

        // full path to the input-capture jsonl the recorder wrote beside this session's files —
        // cursor positions, clicks and keystrokes, which the editor's cursor/keyboard overlays
        // read. It stays in the session directory for the life of the session (like
        // videoedit.json); null on recordings made without input capture, and the editor degrades
        // to no-data when the file has since gone missing.
        public string InputCapturePath
        {
            get => Get<string>();
            set => Set(value);
        }

        // the recording's audio tracks as the recorder described them, written once when the session
        // is created (like WebcamTrack). Null on anything it did not report; the video editor still
        // builds a row per audio stream it probes, and uses these only to name them.
        public SessionAudioTrack[] AudioTracks
        {
            get => Get<SessionAudioTrack[]>();
            set => Set(value);
        }

        [JsonIgnore] public bool IsVideo => String.Equals(ContentKind, "video", StringComparison.OrdinalIgnoreCase);

        // set on sessions that are a video *project* rather than a recording: the composition in
        // this directory's videoedit.json is the whole content, and there is no source mp4 behind
        // it (the user started a blank video editor and imported media into it). It stays true
        // after the project has been rendered — the render is an output, in its own entry, and the
        // project is still the thing this session owns.
        public bool IsVideoProject
        {
            get => Get<bool>();
            set
            {
                if (Set(value))
                    RaiseRowAffordances();
            }
        }

        // a recording (and a converted GIF) carries a poster frame in PreviewImgPath, but putting
        // that single still on the clipboard is never what the user meant by copying a video, so
        // video entries offer no Copy at all.
        [JsonIgnore] public bool CanCopy => !IsVideo && !IsProject && !String.IsNullOrEmpty(PreviewImgPath);

        // every entry can be (re-)uploaded as long as it isn't busy and owns some content to send.
        // A project owns no finished file — its render does — so it is never uploadable.
        // Whether the file is still on disk is settled by UploadSourcePath at the point of use —
        // this one is evaluated by a binding on every row.
        [JsonIgnore]
        public bool CanUpload => ActiveUpload == null
                                 && ActiveGifConversion == null
                                 && ActiveRender == null
                                 && !IsProject
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
                    RaiseRowAffordances();
            }
        }

        // a GIF can be made from any playable video entry except one that is itself a GIF — so
        // from a Screen Video or a Rendered Video, but never from a project (there is no flattened
        // picture to convert until it has been rendered).
        [JsonIgnore] public bool CanCreateGif => CanPlay && String.IsNullOrEmpty(SourceVideoPath);

        // set only on "Edited" sessions: the recording this session's video was rendered from. It
        // ties the edited entry back to its source, so re-rendering the same recording replaces
        // that entry instead of piling up a new one. Deliberately NOT SourceVideoPath, which means
        // "this session is a GIF" and switches CanCreateGif off.
        public string EditSourceVideoPath
        {
            get => Get<string>();
            set
            {
                if (Set(value))
                    RaiseRowAffordances();
            }
        }

        // set on recordings captured with composition turned off: obs-express was run without
        // --multi-track and wrote one flattened track, so there are no separate streams for the
        // editor to trim, place or mix. Written once, when the session is created. Absent (false)
        // on recordings made before composition was a choice — those stay editable, which is what
        // they did before this flag existed.
        public bool SingleTrack
        {
            get => Get<bool>();
            set
            {
                if (Set(value))
                    RaiseRowAffordances();
            }
        }

        /// <summary>
        /// Whether this entry is a video <b>project</b> rather than a finished video: a blank edit
        /// started from the Video button, or a recording captured with composition on. The latter's
        /// mp4 holds one stream per track and is kept in the session directory — nothing can play
        /// it as a video, so the row offers Edit and Render instead of Play, GIF and Upload, and
        /// the rendered output is a separate entry linked back to this one.
        /// </summary>
        [JsonIgnore]
        public bool IsProject => IsVideoProject
                                 || (IsVideo && !SingleTrack
                                     && String.IsNullOrEmpty(SourceVideoPath)
                                     && String.IsNullOrEmpty(EditSourceVideoPath)
                                     && !String.IsNullOrEmpty(VideoPath));

        /// <summary>A video entry with a flat picture behind it: a Screen Video, a Rendered Video,
        /// a GIF, or a video someone uploaded. What Play, Show in folder and Create GIF need.</summary>
        [JsonIgnore]
        public bool CanPlay => IsVideo && !IsProject && !String.IsNullOrEmpty(VideoPath);

        // whether the Recent page offers an Edit button at all. Not gated on the OS — the editor
        // and the render tool run on every desktop platform; when one of them cannot start,
        // VideoEditorWindow says why.
        [JsonIgnore]
        public bool ShowEditVideo => IsProject;

        // kept as its own name because a good deal of code asks "can this be opened in the video
        // editor?" rather than "does the row show the button?" — for a project they are the same.
        [JsonIgnore]
        public bool CanEditVideo => ShowEditVideo;

        /// <summary>Whether the row offers a Render button: a project is rendered into a video, and
        /// nothing else is.</summary>
        [JsonIgnore]
        public bool ShowRender => IsProject;

        /// <summary>Whether the row offers "Open in editor" — the <i>image</i> editor, which a
        /// project has no business opening in.</summary>
        [JsonIgnore]
        public bool ShowOpen => !IsUploadOnly && !IsProject;

        /// <summary>Whether the row offers "Show in folder": exactly when there is a file for
        /// ShowInFolderClicked to reveal — the recording for a video entry or a project, the image
        /// for a capture. Deliberately NOT gated on IsUploadOnly, which is true of every video
        /// session (ContentKind "video"); a text/file upload-only session is excluded anyway,
        /// because its payload sits beside session.json under neither of these names.</summary>
        [JsonIgnore]
        public bool CanShowInFolder => !String.IsNullOrEmpty(VideoPath) || !String.IsNullOrEmpty(PreviewImgPath);

        /// <summary>What a render of this session keys its output entry to: the recording for a
        /// capture, the session file itself for a project that has no recording behind it. Only
        /// ever compared, never opened.</summary>
        [JsonIgnore]
        public string RenderSourceKey => String.IsNullOrEmpty(VideoPath) ? FilePath : VideoPath;

        /// <summary>What the project marker beside a project's name explains when hovered.</summary>
        [JsonIgnore]
        public string ProjectTooltip =>
            "This is a video project, not a finished video — open it in the editor, or render it into a video.";

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
        // than "Not uploaded" over the top of its own progress row. A project is never uploaded —
        // that line is where its render status goes instead (see RenderStatusText).
        [JsonIgnore]
        public bool ShowNotUploaded => !IsProject && ActiveGifConversion == null && ActiveRender == null
                                       && ActiveUpload == null && AllUploads.Length == 0;

        /// <summary>
        /// Not persisted — what a project row says where every other row says "Not uploaded":
        /// "Not rendered", or "Rendered on …" once its render has landed. Null on anything that is
        /// not a project, and while a render of it is in flight (the render's own row, chained
        /// directly above this one, is already showing the progress bar).
        /// Written by the Recent page as it lays the list out, which is the only place that knows
        /// which entry was rendered from which.
        /// </summary>
        [JsonIgnore]
        public string RenderStatusText
        {
            get => _renderStatusText;
            set
            {
                if (_renderStatusText == value)
                    return; // the page recomputes this on every rebuild, and a rebuild is what a
                            // property change here triggers — only a real change may be announced
                _renderStatusText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowRenderStatus));
            }
        }

        [JsonIgnore] public bool ShowRenderStatus => !String.IsNullOrEmpty(RenderStatusText);

        /// <summary>
        /// Not persisted — whether the row directly above this one on the Recent page was made
        /// <i>from</i> this one (a render above its project, a GIF above the video it was converted
        /// from). Such a row draws the upper half of the bracket joining the two in the list's left
        /// gutter, plus the chain-link glyph that sits on the join. Written by the Recent page for
        /// the same reason as <see cref="RenderStatusText"/>.
        /// </summary>
        [JsonIgnore]
        public bool LinkedToPrevious
        {
            get => _linkedToPrevious;
            set
            {
                if (_linkedToPrevious == value)
                    return; // see RenderStatusText
                _linkedToPrevious = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Not persisted — the other end of <see cref="LinkedToPrevious"/>: whether this row was
        /// made from the row directly below it. Such a row draws the lower half of the bracket.
        /// Each row owning its own half is what lets the bracket meet both previews dead centre
        /// however tall either row happens to be — neither half needs to know the other's height.
        /// </summary>
        [JsonIgnore]
        public bool LinkedToNext
        {
            get => _linkedToNext;
            set
            {
                if (_linkedToNext == value)
                    return; // see RenderStatusText
                _linkedToNext = value;
                OnPropertyChanged();
            }
        }

        /// <summary>True when <paramref name="path"/> lives inside this session's own directory —
        /// what tells a recording kept with its session apart from one saved to the user's output
        /// folder. An unreadable path counts as inside: the cautious answer everywhere this is
        /// asked.</summary>
        public bool IsInsideSessionDirectory(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                return String.IsNullOrEmpty(path)
                       || (!String.IsNullOrEmpty(dir)
                           && Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Announces every derived flag the Recent row's buttons, marker and status line
        /// are bound to. Raised by each of the few persisted fields that classify an entry — which
        /// of them changed is never interesting, and missing one silently leaves a stale row.</summary>
        private void RaiseRowAffordances()
        {
            OnPropertyChanged(nameof(IsVideo));
            OnPropertyChanged(nameof(IsUploadOnly));
            OnPropertyChanged(nameof(IsProject));
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(CanCopy));
            OnPropertyChanged(nameof(CanUpload));
            OnPropertyChanged(nameof(CanCreateGif));
            OnPropertyChanged(nameof(ShowEditVideo));
            OnPropertyChanged(nameof(CanEditVideo));
            OnPropertyChanged(nameof(ShowRender));
            OnPropertyChanged(nameof(ShowOpen));
            OnPropertyChanged(nameof(CanShowInFolder));
            OnPropertyChanged(nameof(ShowNotUploaded));
        }

        private Clowd.UI.ActiveUpload _activeUpload;
        private Clowd.UI.Services.GifConversion _activeGifConversion;
        private Clowd.UI.Services.VideoRender _activeRender;
        private string _renderStatusText;
        private bool _linkedToPrevious;
        private bool _linkedToNext;
    }
}
