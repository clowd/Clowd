using System;
using System.Collections.Generic;
using System.IO;
using Clowd.UI.VideoEditor;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;

namespace Clowd.UI.Services
{
    /// <summary>
    /// The one recipe for "the project this session directory opens as" — the saved
    /// <c>videoedit.json</c> beside it, or the identity edit of its recording when there is no
    /// saved one yet. The renderer and the recents preview engine both go through here so the two
    /// cannot drift into composing different pictures of the same session.
    ///
    /// Deliberately synchronous and <see cref="SessionInfo"/>-free. The preview engine's workers
    /// build projects off the UI thread and must never dereference a session:
    /// <c>SessionManager.DeleteSession</c> disposes them, and a disposed <c>FileSyncObject</c>
    /// throws from every accessor. So everything the recipe needs arrives as plain values that the
    /// caller read on the thread that owns the session — the two decoration objects
    /// (<paramref name="audioTrackNames"/>, <paramref name="hints"/>) are immutable snapshots by
    /// the time they get here.
    ///
    /// The caller must already have initialized FFmpeg (the renderer through
    /// <c>FFmpegLoader.TryInitialize</c>, the preview engine through its own gate) — the probe
    /// needs it.
    /// </summary>
    public static class SessionProjectBuilder
    {
        /// <summary>
        /// Builds the project for a session directory. Returns false, with a null
        /// <paramref name="project"/>, when there is nothing to build one from — a recording whose
        /// file carries no video stream. That is the only "no" this method has; it is otherwise
        /// exactly as loud as the persistence layer underneath it.
        ///
        /// <b>Try in the shape of its result, not in the shape of its failures.</b> A corrupt edit
        /// document, an unreadable directory or a probe that cannot open the file still <i>throw</i>
        /// — the renderer turns that into a message naming the reason, while a preview producer
        /// swallows it and falls back to an icon, and neither can be served by a bool. Do not add a
        /// catch here.
        /// </summary>
        /// <param name="sessionDir">The session directory, i.e.
        /// <c>Path.GetDirectoryName(session.FilePath)</c>. May be null or empty (the dev harness
        /// opens files with no session behind them); the edit document is then simply not looked
        /// for and a fresh project is built.</param>
        /// <param name="videoPath">The recording to build the identity edit from. Unused when
        /// <paramref name="isVideoProject"/> is true — a blank project owns nothing but its edit
        /// document, so there is no recording to probe.</param>
        /// <param name="isVideoProject">The session's <c>IsVideoProject</c>: a project started from
        /// the Video button rather than opened onto a capture.</param>
        /// <param name="audioTrackNames">Row names for the recording's audio streams, index-aligned
        /// (see <c>AudioTrackLabels.From</c>). Decoration over the probe and build-time only; null
        /// is fine and is what a caller with no session passes.</param>
        /// <param name="hints">The session's classification of the recording's video streams and
        /// its input-capture file (see <see cref="RecordingTrackHints"/>). Same terms as the labels
        /// — decoration, build-time only, null is fine.</param>
        public static bool TryBuild(string sessionDir, string videoPath, bool isVideoProject,
            IReadOnlyList<string> audioTrackNames, RecordingTrackHints hints, out Project project)
        {
            var editDocPath = String.IsNullOrEmpty(sessionDir)
                ? null
                : Path.Combine(sessionDir, VideoEditPersistence.FileName);

            // a blank project owns nothing but its edit document — there is no recording to probe.
            if (isVideoProject)
            {
                project = VideoEditPersistence.LoadOrCreateBlank(editDocPath);
                return project != null;
            }

            var probe = MediaProbe.ProbeDetailed(videoPath);
            if (probe.VideoStreams == null || probe.VideoStreams.Count == 0)
            {
                project = null;
                return false;
            }

            project = VideoEditPersistence.LoadOrCreate(editDocPath, videoPath, probe, audioTrackNames, hints);
            return project != null;
        }

        /// <summary>The undecorated build, for a caller that has no session to read labels and
        /// hints off — the preview engine. Both are build-time decoration the probe overrides
        /// anyway, and a session that has ever been saved carries them inside its own edit
        /// document, so a preview of it is unaffected by their absence.</summary>
        public static bool TryBuild(string sessionDir, string videoPath, bool isVideoProject, out Project project)
            => TryBuild(sessionDir, videoPath, isVideoProject, null, null, out project);
    }
}
