using System;
using Clowd.Config;

namespace Clowd.UI.Services
{
    /// <summary>
    /// Everything a single video render is asked to do that the project itself does not carry: the
    /// encoder quality, the encode-time size cap, where the file goes, and what should happen once
    /// it is there. One of these is built by the Render flyout (from a preset) or by the "Render
    /// video" dialog and handed to <see cref="VideoRenderManager.StartRenderAsync(Clowd.SessionInfo, Clowd.VideoSDK.Model.Project, RenderRequest)"/>;
    /// the dev harness (<c>--video-edit</c>) runs the same request in-process.
    /// </summary>
    public sealed record RenderRequest
    {
        /// <summary>The x264 CRF the video is encoded with, 0-51, lower being better quality and a
        /// bigger file. The three presets use <see cref="VideoQuality"/>'s values.</summary>
        public int Crf { get; init; } = (int)VideoQuality.Medium;

        /// <summary>Encode-time cap on the output height in pixels, 0 for none. The project still
        /// composes at its own canvas size — only the encoder is opened smaller, with the aspect
        /// preserved (see <c>RenderJob.CapSize</c>).</summary>
        public int MaxHeight { get; init; }

        /// <summary>Let the GPU encoder (NVENC, AMF or VideoToolbox, whichever opens; x264 when
        /// none does) encode the video instead of x264. Off by default: on a desktop CPU x264 fast
        /// is faster and its file smaller at the same quality setting.</summary>
        public bool HardwareEncoder { get; init; }

        /// <summary>Where to write the mp4, or null to let the render manager derive the default
        /// from the recording settings (output folder + filename pattern). Only the dialog sets
        /// this: a preset render never asks the user where the file goes.</summary>
        public string OutputPath { get; init; }

        /// <summary>Put the finished file on the clipboard as a file-drop list.</summary>
        public bool CopyToClipboard { get; init; }

        /// <summary>Reveal the finished file in the file manager.</summary>
        public bool ShowInFolder { get; init; }
    }

    /// <summary>
    /// The encoder settings behind the three rows of the Render flyout, and the way back: which row
    /// (if any) a pair of dialog values amounts to. Settings only — the titles and captions the user
    /// reads are the flyout's own rows in <c>VideoEditorWindow.axaml</c>.
    /// </summary>
    public static class RenderPresets
    {
        /// <summary>The CRF a preset encodes with. Custom has none of its own — the caller reads
        /// the remembered <see cref="SettingsVideoEditor.CustomRenderCrf"/> instead — so it maps to
        /// the Share value, which is what the dialog opens on the first time.</summary>
        public static int CrfOf(RenderPreset preset) => preset switch
        {
            RenderPreset.BestQuality => (int)VideoQuality.High,
            RenderPreset.SmallFile => (int)VideoQuality.Low,
            _ => (int)VideoQuality.Medium,
        };

        /// <summary>The preset's encode-time height cap, 0 for none.</summary>
        public static int MaxHeightOf(RenderPreset preset) => preset == RenderPreset.SmallFile ? 720 : 0;

        /// <summary>Which preset (if any) a pair of dialog values is: used after a custom render so
        /// the flyout can still check the row the user effectively picked, and only falls back to
        /// <see cref="RenderPreset.Custom"/> when the values match none of the three.</summary>
        public static RenderPreset Match(int crf, int maxHeight)
        {
            foreach (var preset in new[] { RenderPreset.Share, RenderPreset.BestQuality, RenderPreset.SmallFile })
            {
                if (CrfOf(preset) == crf && MaxHeightOf(preset) == maxHeight)
                    return preset;
            }

            return RenderPreset.Custom;
        }

        /// <summary>
        /// The request a flyout row starts: the preset's encoder settings, the encoder and
        /// after-render choices the user last ticked in the dialog, and no output path (the manager derives the default
        /// from the recording settings, exactly as every render did before the dialog existed).
        /// A <see cref="RenderPreset.Custom"/> preset takes the remembered dialog values instead.
        /// </summary>
        public static RenderRequest Create(RenderPreset preset, SettingsVideoEditor settings, string outputPath = null)
        {
            var custom = preset == RenderPreset.Custom;
            return new RenderRequest
            {
                Crf = custom && settings != null ? ClampCrf(settings.CustomRenderCrf) : CrfOf(preset),
                MaxHeight = custom && settings != null ? Math.Max(0, settings.CustomRenderMaxHeight) : MaxHeightOf(preset),
                OutputPath = outputPath,
                HardwareEncoder = settings?.HardwareEncodeRender ?? false,
                CopyToClipboard = settings?.CopyToClipboardAfterRender ?? false,
                ShowInFolder = settings?.ShowInFolderAfterRender ?? false,
            };
        }

        /// <summary>The CRF range x264 accepts. A settings file edited by hand (or written by a
        /// future build) must never reach the encoder out of range.</summary>
        public static int ClampCrf(int crf) => Math.Clamp(crf, MinCrf, MaxCrf);

        /// <summary>Lowest CRF the dialog offers — lossless, enormous.</summary>
        public const int MinCrf = 0;

        /// <summary>Highest CRF the dialog offers.</summary>
        public const int MaxCrf = 51;
    }
}
