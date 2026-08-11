using System;
using System.Collections.Generic;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// Turns a recording's <see cref="SessionAudioTrack"/> list into the row names the editor opens
    /// with. The recorder is the only thing that knows a stream is the microphone rather than the
    /// system mix, but it is <b>not</b> the authority on which streams exist — the probe is. So this
    /// produces nothing but labels, index-aligned with the recording's audio streams: an entry the
    /// project has no stream for is simply never read, and a stream with no entry (or an unrecognized
    /// kind) falls back to <c>RecordingProject</c>'s own "Audio"/"Audio N".
    /// </summary>
    internal static class AudioTrackLabels
    {
        public const string Microphone = "Microphone";
        public const string SystemAudio = "System Audio";

        /// <summary>The label for one recorder <c>kind</c> string, or null when there is no better
        /// name than the numbered fallback — including "mixed", which is what a single-track
        /// recording reports and describes no particular device.</summary>
        public static string ForKind(string kind) => kind?.ToLowerInvariant() switch
        {
            "microphone" => Microphone,
            "speaker" => SystemAudio,
            _ => null,
        };

        /// <summary>
        /// Labels for the recording's audio streams, or null when there is nothing to say. Entries
        /// are placed at the index they claim rather than where they sit in the list — the array is
        /// read positionally against the probed streams, so an out-of-order or gappy report must not
        /// shift the names onto the wrong rows.
        /// </summary>
        public static IReadOnlyList<string> From(IReadOnlyList<SessionAudioTrack> tracks)
        {
            if (tracks == null || tracks.Count == 0)
                return null;

            var length = 0;
            foreach (var track in tracks)
            {
                if (track != null && track.Index >= 0)
                    length = Math.Max(length, track.Index + 1);
            }

            if (length == 0)
                return null;

            var names = new string[length];
            var named = false;
            foreach (var track in tracks)
            {
                if (track == null || track.Index < 0)
                    continue;

                var name = ForKind(track.Kind);
                if (name == null)
                    continue;

                names[track.Index] = name;
                named = true;
            }

            return named ? names : null;
        }
    }
}
