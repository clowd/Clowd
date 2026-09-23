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

        /// <summary>Cap on the output frame rate in whole frames per second, 0 for none. Never
        /// raises the rate: the render is encoded at the project's fastest clip, or at this cap
        /// when it is lower (see <see cref="RenderFrameRate.Resolve(Clowd.VideoSDK.Model.Project, int)"/>).</summary>
        public int MaxFps { get; init; }

        /// <summary>Let the GPU encoder (NVENC, AMF or VideoToolbox, whichever opens; x264 when
        /// none does) encode the video instead of x264. Off by default: on a desktop CPU x264 fast
        /// is faster and its file smaller at the same quality setting.</summary>
        public bool HardwareEncoder { get; init; }

        /// <summary>The file the render writes: MP4 (the default) or MKV. The container follows the
        /// output path's extension, so this is what the default path is given and what the dialog
        /// keeps the "Save to" box ending in; the streams inside are the same either way.</summary>
        public VideoContainer Container { get; init; }

        /// <summary>Where to write the video, or null to let the render manager derive the default
        /// from the recording settings (output folder + filename pattern). Only the dialog sets
        /// this: a preset render never asks the user where the file goes.</summary>
        public string OutputPath { get; init; }

        /// <summary>Put the finished file on the clipboard as a file-drop list.</summary>
        public bool CopyToClipboard { get; init; }

        /// <summary>Reveal the finished file in the file manager.</summary>
        public bool ShowInFolder { get; init; }

        /// <summary>Delete the edited session (the Recents entry the editor was opened on) once the
        /// render has succeeded. Set by the dialog, or by a user preset saved with it ticked; it is
        /// not a standing setting, so the built-in rows never delete anything.</summary>
        public bool DeleteSession { get; init; }

        /// <summary>Save this request's settings (encoder and after-render) as a new preset under
        /// this name when the render starts, or null. Only the dialog sets this.</summary>
        public string SaveAsPresetName { get; init; }
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

        /// <summary>The preset's encode-time height cap, 0 for none: Share stops at 1080p (level 4.2
        /// H.264, which every phone and browser decodes; 1440p/4K60 is not), Small file at 720p, Best
        /// quality keeps the full canvas. A cap at or above the canvas changes nothing.</summary>
        public static int MaxHeightOf(RenderPreset preset) => preset switch
        {
            RenderPreset.Share => 1080,
            RenderPreset.SmallFile => 720,
            _ => 0,
        };

        /// <summary>The preset's frame-rate cap, 0 for none: Share stops at 60 (anything faster
        /// is mostly bytes a chat player drops), Small file at 30, Best quality keeps every frame
        /// the material has. A cap above the project's fastest clip changes nothing.</summary>
        public static int MaxFpsOf(RenderPreset preset) => preset switch
        {
            RenderPreset.Share => 60,
            RenderPreset.SmallFile => 30,
            _ => 0,
        };

        /// <summary>Which preset (if any) a set of dialog values is: used after a custom render so
        /// the flyout can still check the row the user effectively picked, and only falls back to
        /// <see cref="RenderPreset.Custom"/> when the values match none of the three.</summary>
        public static RenderPreset Match(int crf, int maxHeight, int maxFps)
        {
            foreach (var preset in new[] { RenderPreset.Share, RenderPreset.BestQuality, RenderPreset.SmallFile })
            {
                if (CrfOf(preset) == crf && MaxHeightOf(preset) == maxHeight && MaxFpsOf(preset) == maxFps)
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
                MaxFps = custom && settings != null ? ClampFps(settings.CustomRenderMaxFps) : MaxFpsOf(preset),
                OutputPath = outputPath,
                HardwareEncoder = settings?.HardwareEncodeRender ?? false,
                Container = settings?.RenderContainer ?? VideoContainer.Mp4,
                CopyToClipboard = settings?.CopyToClipboardAfterRender ?? false,
                ShowInFolder = settings?.ShowInFolderAfterRender ?? false,
            };
        }

        /// <summary>The request a user preset renders: everything it was saved with, the
        /// after-render actions included. Nothing comes from the standing settings.</summary>
        public static RenderRequest Create(RenderUserPreset preset) => new RenderRequest
        {
            Crf = ClampCrf(preset.Crf),
            MaxHeight = Math.Max(0, preset.MaxHeight),
            MaxFps = ClampFps(preset.MaxFps),
            HardwareEncoder = preset.HardwareEncoder,
            Container = preset.Container,
            CopyToClipboard = preset.CopyToClipboard,
            ShowInFolder = preset.ShowInFolder,
            DeleteSession = preset.DeleteSession,
        };

        /// <summary>The CRF range x264 accepts. A settings file edited by hand (or written by a
        /// future build) must never reach the encoder out of range.</summary>
        public static int ClampCrf(int crf) => Math.Clamp(crf, MinCrf, MaxCrf);

        /// <summary>A frame-rate cap as the request carries it: 0 (none) for anything not
        /// positive, and never past what a preset box can hold.</summary>
        public static int ClampFps(int fps) => fps <= 0 ? 0 : Math.Min(fps, FpsPresets.MaxFps);

        /// <summary>Lowest CRF the dialog offers — lossless, enormous.</summary>
        public const int MinCrf = 0;

        /// <summary>Highest CRF the dialog offers.</summary>
        public const int MaxCrf = 51;
    }
}
