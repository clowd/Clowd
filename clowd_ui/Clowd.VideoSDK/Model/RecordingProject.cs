using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Playback;

namespace Clowd.VideoSDK.Model;

/// <summary>The identities a recording's project is built with. Kept out of
/// <see cref="RecordingProject.Build"/> so a host that rebuilds the project on every edit (the
/// editor does) can hand back the same ids each time — a changed source/stream identity is what
/// makes <c>CompositionPlayer.UpdateProject</c> tear its decoders down and rebuild them.</summary>
public sealed class RecordingIds
{
    public Guid SourceId { get; init; }

    public Guid ScreenTrackId { get; init; }

    public Guid WebcamTrackId { get; init; }

    public Guid AudioTrackId { get; init; }

    /// <summary>One recording is one link group: every row it produced trims/cuts as one.</summary>
    public Guid LinkGroupId { get; init; }

    public static RecordingIds New() => new RecordingIds
    {
        SourceId = Guid.NewGuid(),
        ScreenTrackId = Guid.NewGuid(),
        WebcamTrackId = Guid.NewGuid(),
        AudioTrackId = Guid.NewGuid(),
        LinkGroupId = Guid.NewGuid(),
    };
}

/// <summary>One kept slice of the recording, in source time: <c>[SourceInTicks, +DurationTicks)</c>.
/// The slices are placed back to back on the output timeline in the order given.</summary>
public readonly record struct KeepSegment(long SourceInTicks, long DurationTicks);

/// <summary>What <see cref="RecordingProject.Build"/> should make of one recording.</summary>
public sealed class RecordingProjectSpec
{
    /// <summary>Full path to the recording (the single <see cref="Source"/> of the project).</summary>
    public string InputPath { get; set; }

    /// <summary>The screen stream — track 0, and the stream whose size is the output canvas.</summary>
    public VideoStreamProbe Screen { get; set; }

    /// <summary>The webcam stream, or null when the recording carries none.</summary>
    public VideoStreamProbe Webcam { get; set; }

    /// <summary>The audio stream, or null.</summary>
    public AudioStreamProbe Audio { get; set; }

    public int FpsNum { get; set; }

    public int FpsDen { get; set; } = 1;

    /// <summary>The kept slices, in source order, placed back to back on the timeline.</summary>
    public IReadOnlyList<KeepSegment> Segments { get; set; }

    /// <summary>Placement of the webcam items on the canvas; null = the default (full frame).</summary>
    public Transform WebcamTransform { get; set; }

    /// <summary>Whether the webcam row is excluded from the picture (the editor's "show webcam
    /// overlay" toggle, off). The items stay on the timeline so turning it back on cannot lose
    /// the placement.</summary>
    public bool WebcamHidden { get; set; }

    /// <summary>Ids to build with; null mints fresh ones.</summary>
    public RecordingIds Ids { get; set; }
}

/// <summary>
/// The one mapping from "a Clowd recording plus a keep-segment list" onto a v2
/// <see cref="Project"/>: three rows (screen video, optional webcam video, optional audio) over a
/// single source file, one item per kept slice per row, all sharing one
/// <see cref="Item.LinkGroupId"/>.
///
/// Both entry points into the editor's world go through here so they cannot drift: the v1 args
/// shim (<c>RenderArgsCompat</c>, which maps a legacy <c>render-args.json</c>) and the editor
/// itself (which maps its trim/cut document, rebuilding on every edit). The webcam geometry
/// helper (<see cref="WebcamTransform"/>) is shared for the same reason — the editor computes the
/// same pixel rect the render tool is given and normalizes it through this method, which is what
/// makes the preview's placement identical to the rendered one.
/// </summary>
public static class RecordingProject
{
    /// <summary>Frame rate used when the input declares none at all (neither avg_frame_rate nor
    /// r_frame_rate). Output is CFR, so a rate must be picked; 30/1 matches the recorder's
    /// default.</summary>
    public const int FallbackFpsNum = 30;

    /// <summary>Output sample rate used when the input has no audio stream to take one from.</summary>
    public const int FallbackSampleRate = 48000;

    /// <summary>Output is CFR (the model composes N sources, so no source timing survives):
    /// take avg_frame_rate, fall back to r_frame_rate, then to <see cref="FallbackFpsNum"/>.</summary>
    public static (int Num, int Den) ChooseFrameRate(VideoStreamProbe screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (screen.AvgFrameRateNum > 0 && screen.AvgFrameRateDen > 0)
            return (screen.AvgFrameRateNum, screen.AvgFrameRateDen);
        if (screen.RFrameRateNum > 0 && screen.RFrameRateDen > 0)
            return (screen.RFrameRateNum, screen.RFrameRateDen);
        return (FallbackFpsNum, 1);
    }

    /// <summary>
    /// Normalizes a webcam overlay rectangle given in <b>screen-frame pixels</b> into the model's
    /// canvas-relative geometry: centre over the frame size, width as a fraction of the frame
    /// width. The rect's height is redundant under the model — the item's height follows the
    /// camera's own aspect ratio — and is taken only for the centre.
    /// </summary>
    public static Transform WebcamTransform(int rectX, int rectY, int rectW, int rectH,
        int frameWidth, int frameHeight, Mask mask)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frameWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frameHeight, 0);

        return new Transform
        {
            X = (rectX + rectW / 2.0) / frameWidth,
            Y = (rectY + rectH / 2.0) / frameHeight,
            Scale = rectW / (double)frameWidth,
            Mask = mask,
        };
    }

    /// <summary>
    /// Builds the project. The result is <see cref="Project.Normalize"/>d but not validated —
    /// callers that accept untrusted input (the v1 shim) run <see cref="Project.Validate"/>
    /// themselves so they can phrase the failure in their own terms.
    /// </summary>
    public static Project Build(RecordingProjectSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Screen);
        ArgumentNullException.ThrowIfNull(spec.Segments);

        var screen = spec.Screen;
        var cam = spec.Webcam;
        var audio = spec.Audio;
        var ids = spec.Ids ?? RecordingIds.New();

        var project = new Project
        {
            Output = new OutputSettings
            {
                WidthPx = screen.Width,
                HeightPx = screen.Height,
                FpsNum = spec.FpsNum,
                FpsDen = spec.FpsDen,
                SampleRate = audio != null && audio.SampleRate > 0 ? audio.SampleRate : FallbackSampleRate,
            },
        };

        var source = new Source { Id = ids.SourceId, Path = spec.InputPath };
        source.Streams.Add(ToSourceStream(screen));
        if (cam != null)
            source.Streams.Add(ToSourceStream(cam));
        if (audio != null)
            source.Streams.Add(new SourceStream
            {
                Index = audio.StreamIndex,
                Kind = StreamKind.Audio,
                DurationTicks = audio.DurationTicks,
            });
        project.Sources.Add(source);

        var screenTrack = AddTrack(project, ids.ScreenTrackId, TrackKind.Video, "Screen", 0, hidden: false);
        var camTrack = cam != null
            ? AddTrack(project, ids.WebcamTrackId, TrackKind.Video, "Webcam", 1, spec.WebcamHidden)
            : null;
        var audioTrack = audio != null
            ? AddTrack(project, ids.AudioTrackId, TrackKind.Audio, "Audio", 2, hidden: false)
            : null;

        var camTransform = spec.WebcamTransform;

        long timelineStart = 0;
        foreach (var segment in spec.Segments)
        {
            long startTicks = segment.SourceInTicks;
            long durationTicks = segment.DurationTicks;
            if (durationTicks <= 0)
                continue;

            AddItem(project, screenTrack, source.Id, screen.StreamIndex, timelineStart, durationTicks,
                startTicks, ids.LinkGroupId, null);

            if (camTrack != null)
                AddItem(project, camTrack, source.Id, cam.StreamIndex, timelineStart, durationTicks,
                    startTicks, ids.LinkGroupId, camTransform?.Clone());

            if (audioTrack != null)
            {
                // Audio only where the source audio exists: real recordings' audio track ends a
                // few hundredths of a second before the video, and the v1 atrim chain ended the
                // output audio there rather than padding silence to the video's end.
                long audioDuration = durationTicks;
                if (audio.DurationTicks > 0)
                    audioDuration = Math.Clamp(audio.DurationTicks - startTicks, 0, durationTicks);
                if (audioDuration > 0)
                    AddItem(project, audioTrack, source.Id, audio.StreamIndex, timelineStart,
                        audioDuration, startTicks, ids.LinkGroupId, null);
            }

            timelineStart += durationTicks;
        }

        project.Normalize();
        return project;
    }

    private static SourceStream ToSourceStream(VideoStreamProbe s) => new SourceStream
    {
        Index = s.StreamIndex,
        Kind = StreamKind.Video,
        Width = s.Width,
        Height = s.Height,
        AvgFrameRateNum = s.AvgFrameRateNum,
        AvgFrameRateDen = s.AvgFrameRateDen,
        IsVariableFrameRate = s.IsVariableFrameRate,
        StartTimeTicks = s.StartTimeTicks,
        DurationTicks = s.DurationTicks,
    };

    private static Track AddTrack(Project project, Guid id, TrackKind kind, string name, int order, bool hidden)
    {
        var track = new Track { Id = id, Kind = kind, Name = name, Order = order, Hidden = hidden };
        project.Tracks.Add(track);
        return track;
    }

    private static void AddItem(Project project, Track track, Guid sourceId, int streamIndex,
        long timelineStartTicks, long durationTicks, long sourceInTicks, Guid linkGroup,
        Transform transform)
    {
        project.Items.Add(new Item
        {
            Id = Guid.NewGuid(),
            TrackId = track.Id,
            TimelineStartTicks = timelineStartTicks,
            DurationTicks = durationTicks,
            Content = new MediaContent
            {
                SourceId = sourceId,
                StreamIndex = streamIndex,
                SourceInTicks = sourceInTicks,
            },
            Transform = transform ?? new Transform(),
            LinkGroupId = linkGroup,
        });
    }
}
