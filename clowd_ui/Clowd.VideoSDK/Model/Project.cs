using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// The version-2 composition project: a fixed output canvas, the media files feeding it
/// (<see cref="Sources"/>), the timeline rows (<see cref="Tracks"/>) and a <b>flat</b> list of
/// <see cref="Items"/> keyed by <see cref="Item.TrackId"/> — flat rather than nested in tracks so
/// selection, undo and the cross-track link operations in <see cref="TimelineOps"/> stay simple.
///
/// This is both the project file and the wire format handed to <c>Clowd.VideoRender</c>. Version 1
/// is the legacy <c>videoedit.json</c> (<c>VideoEditDocument</c>), migrated one-way into this shape.
///
/// All time fields across the model are in 100ns ticks — the unit of
/// <see cref="TimeSpan.Ticks"/> and of <c>VideoDecodeWorker.PtsToTicks</c> — and frame rates are
/// rational (num/den), never a double.
/// </summary>
public sealed class Project
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public OutputSettings Output { get; set; } = new OutputSettings();

    public List<Source> Sources { get; set; } = new List<Source>();

    public List<Track> Tracks { get; set; } = new List<Track>();

    /// <summary>All timeline items, across every track. See the class remarks for why this is a
    /// flat list. Kept sorted by (track order, start time) by <see cref="Normalize"/>.</summary>
    public List<Item> Items { get; set; } = new List<Item>();

    /// <summary>Serializes with the source-generated context (indented — the file is read by
    /// humans when a render goes wrong).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ProjectJsonContext.Default.Project);

    /// <summary>Round-trip counterpart of <see cref="ToJson"/>. Throws
    /// <see cref="JsonException"/> on malformed input.</summary>
    public static Project FromJson(string json) =>
        JsonSerializer.Deserialize(json, ProjectJsonContext.Default.Project);

    /// <summary>The timeline's length in 100ns ticks: the maximum <see cref="Item.TimelineEndTicks"/>
    /// across all items, 0 for an empty project. This is the duration the renderer produces —
    /// output runs to the end of the last item, wherever it sits.</summary>
    public long GetDurationTicks()
    {
        long max = 0;
        if (Items != null)
        {
            foreach (var item in Items)
                max = Math.Max(max, item.TimelineEndTicks);
        }
        return max;
    }

    /// <summary>
    /// Puts the model into canonical order without changing what it means: null collections become
    /// empty, tracks sort by <see cref="Track.Order"/> (then Id, so the sort is total), and items
    /// sort by their track's position, then start time, then Id. Call after mutating the model so
    /// serialization and iteration order are deterministic. Normalization never rejects anything —
    /// that is <see cref="Validate"/>'s job.
    /// </summary>
    public void Normalize()
    {
        Output ??= new OutputSettings();
        Sources ??= new List<Source>();
        Tracks ??= new List<Track>();
        Items ??= new List<Item>();

        foreach (var source in Sources)
            source.Streams ??= new List<SourceStream>();

        Tracks.Sort((a, b) =>
        {
            var byOrder = a.Order.CompareTo(b.Order);
            return byOrder != 0 ? byOrder : a.Id.CompareTo(b.Id);
        });

        var trackRank = new Dictionary<Guid, int>();
        for (var i = 0; i < Tracks.Count; i++)
            trackRank.TryAdd(Tracks[i].Id, i);

        Items.Sort((a, b) =>
        {
            var rankA = trackRank.TryGetValue(a.TrackId, out var ra) ? ra : int.MaxValue;
            var rankB = trackRank.TryGetValue(b.TrackId, out var rb) ? rb : int.MaxValue;
            if (rankA != rankB)
                return rankA.CompareTo(rankB);

            var byStart = a.TimelineStartTicks.CompareTo(b.TimelineStartTicks);
            return byStart != 0 ? byStart : a.Id.CompareTo(b.Id);
        });
    }

    /// <summary>
    /// Checks the model is renderable: version supported, output sane, all ids unique, every
    /// item's <see cref="Item.TrackId"/> and <see cref="MediaContent.SourceId"/>/stream resolvable,
    /// geometry non-negative, and no two items on the same track overlapping in time. Returns the
    /// list of problems — empty means valid. Reports everything it finds rather than stopping at
    /// the first error.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Version != CurrentVersion)
            errors.Add($"Unsupported project version {Version} (expected {CurrentVersion}).");

        var output = Output;
        if (output == null)
            errors.Add("Output settings are missing.");
        else
        {
            if (output.WidthPx <= 0 || output.HeightPx <= 0)
                errors.Add($"Output canvas {output.WidthPx}x{output.HeightPx} is not a positive size.");
            if (output.FpsNum <= 0 || output.FpsDen <= 0)
                errors.Add($"Output frame rate {output.FpsNum}/{output.FpsDen} is not a positive rational.");
            if (output.SampleRate <= 0)
                errors.Add($"Output sample rate {output.SampleRate} is not positive.");
        }

        var sources = Sources ?? new List<Source>();
        var tracks = Tracks ?? new List<Track>();
        var items = Items ?? new List<Item>();

        var sourceById = new Dictionary<Guid, Source>();
        foreach (var source in sources)
        {
            if (!sourceById.TryAdd(source.Id, source))
                errors.Add($"Duplicate source id {source.Id}.");
        }

        var trackById = new Dictionary<Guid, Track>();
        foreach (var track in tracks)
        {
            if (!trackById.TryAdd(track.Id, track))
                errors.Add($"Duplicate track id {track.Id}.");
        }

        var itemIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (!itemIds.Add(item.Id))
                errors.Add($"Duplicate item id {item.Id}.");

            trackById.TryGetValue(item.TrackId, out var track);
            if (track == null)
                errors.Add($"Item {item.Id} references unknown track {item.TrackId}.");

            if (item.TimelineStartTicks < 0)
                errors.Add($"Item {item.Id} starts before the timeline origin ({item.TimelineStartTicks} ticks).");
            if (item.DurationTicks <= 0)
                errors.Add($"Item {item.Id} has a non-positive duration ({item.DurationTicks} ticks).");

            switch (item.Content)
            {
                case null:
                    errors.Add($"Item {item.Id} has no content.");
                    break;

                case MediaContent media:
                    if (!sourceById.TryGetValue(media.SourceId, out var source))
                        errors.Add($"Item {item.Id} references unknown source {media.SourceId}.");
                    else if (source.Streams == null || !source.Streams.Any(s => s.Index == media.StreamIndex))
                        errors.Add($"Item {item.Id} references stream {media.StreamIndex} which source {media.SourceId} does not have.");
                    if (media.SourceInTicks < 0)
                        errors.Add($"Item {item.Id} has a negative source in-point ({media.SourceInTicks} ticks).");
                    break;

                default:
                    // text / image / solid are picture content — meaningless on an audio row.
                    if (track is { Kind: TrackKind.Audio })
                        errors.Add($"Item {item.Id} places {item.Content.GetType().Name} on audio track {track.Id}.");
                    break;
            }
        }

        // overlap check per track, only over items whose geometry survived the checks above —
        // a negative-duration item would make the interval math meaningless.
        foreach (var group in items.Where(i => i.DurationTicks > 0 && i.TimelineStartTicks >= 0)
                                   .GroupBy(i => i.TrackId))
        {
            var ordered = group.OrderBy(i => i.TimelineStartTicks).ThenBy(i => i.Id).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var next = ordered[i];
                if (next.TimelineStartTicks < prev.TimelineEndTicks)
                    errors.Add($"Items {prev.Id} and {next.Id} overlap on track {group.Key}.");
            }
        }

        return errors;
    }
}

/// <summary>The fixed output canvas and clock every item is composed into. The frame rate is a
/// rational (<see cref="FpsNum"/>/<see cref="FpsDen"/>) so 29.97/23.976 material renders without
/// drift — frame <c>n</c>'s time is <c>n * FpsDen / FpsNum</c> seconds, computed in integer
/// tick math, never through a double.</summary>
public sealed class OutputSettings
{
    public int WidthPx { get; set; }

    public int HeightPx { get; set; }

    public int FpsNum { get; set; }

    public int FpsDen { get; set; } = 1;

    /// <summary>Audio output sample rate in Hz.</summary>
    public int SampleRate { get; set; }
}
