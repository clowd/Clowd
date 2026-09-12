using System;
using System.IO;
using Clowd.Config;
using Clowd.UI.Helpers;
using Clowd.UI.Services;
using Clowd.UI.VideoEditor;
using Clowd.VideoSDK.Render;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The render UI's decisions that do not need a window: the preset table behind the Render
    /// flyout, the "Save to" box's path rules, and the toast wording after a render. Framework-free
    /// — nothing here constructs an Avalonia control.
    /// </summary>
    public class RenderRequestTests
    {
        [Theory]
        [InlineData(RenderPreset.Share, 23, 0)]
        [InlineData(RenderPreset.BestQuality, 18, 0)]
        [InlineData(RenderPreset.SmallFile, 29, 720)]
        public void Each_preset_carries_the_quality_and_cap_the_flyout_promises(RenderPreset preset, int crf, int maxHeight)
        {
            Assert.Equal(crf, RenderPresets.CrfOf(preset));
            Assert.Equal(maxHeight, RenderPresets.MaxHeightOf(preset));

            var request = RenderPresets.Create(preset, new SettingsVideoEditor());
            Assert.Equal(crf, request.Crf);
            Assert.Equal(maxHeight, request.MaxHeight);
            // a preset render never names a path: the manager derives the settings default.
            Assert.Null(request.OutputPath);
        }

        [Fact]
        public void A_preset_render_takes_the_remembered_after_actions()
        {
            var settings = new SettingsVideoEditor { CopyToClipboardAfterRender = true, ShowInFolderAfterRender = true };
            var request = RenderPresets.Create(RenderPreset.Share, settings);

            Assert.True(request.CopyToClipboard);
            Assert.True(request.ShowInFolder);
        }

        [Fact]
        public void The_custom_preset_reopens_on_the_values_the_dialog_last_rendered()
        {
            var settings = new SettingsVideoEditor { CustomRenderCrf = 20, CustomRenderMaxHeight = 1080 };
            var request = RenderPresets.Create(RenderPreset.Custom, settings);

            Assert.Equal(20, request.Crf);
            Assert.Equal(1080, request.MaxHeight);
        }

        [Fact]
        public void A_settings_file_with_an_impossible_crf_is_clamped_before_it_reaches_the_encoder()
        {
            Assert.Equal(0, RenderPresets.Create(RenderPreset.Custom, new SettingsVideoEditor { CustomRenderCrf = -5 }).Crf);
            Assert.Equal(51, RenderPresets.Create(RenderPreset.Custom, new SettingsVideoEditor { CustomRenderCrf = 99 }).Crf);
            Assert.Equal(0, RenderPresets.Create(RenderPreset.Custom, new SettingsVideoEditor { CustomRenderMaxHeight = -16 }).MaxHeight);
        }

        [Fact]
        public void Null_settings_still_produce_a_runnable_request()
        {
            var request = RenderPresets.Create(RenderPreset.SmallFile, null);

            Assert.Equal(29, request.Crf);
            Assert.Equal(720, request.MaxHeight);
            Assert.False(request.CopyToClipboard);
            Assert.False(request.ShowInFolder);
        }

        [Fact]
        public void Dialog_values_map_back_to_the_row_the_flyout_should_check()
        {
            Assert.Equal(RenderPreset.Share, RenderPresets.Match(23, 0));
            Assert.Equal(RenderPreset.BestQuality, RenderPresets.Match(18, 0));
            Assert.Equal(RenderPreset.SmallFile, RenderPresets.Match(29, 720));
            // the same quality with a different cap is not that preset any more
            Assert.Equal(RenderPreset.Custom, RenderPresets.Match(29, 0));
            Assert.Equal(RenderPreset.Custom, RenderPresets.Match(20, 1080));
        }

        [Fact]
        public void A_typed_name_lands_in_the_default_folder_as_an_mp4()
        {
            var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

            var resolved = RenderOutputPath.Resolve("holiday", dir, out var problem);

            Assert.Null(problem);
            Assert.Equal(Path.Combine(dir, "holiday.mp4"), resolved);
        }

        [Fact]
        public void A_full_path_keeps_its_folder_and_gains_the_extension_only_when_it_lacks_one()
        {
            var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            var other = Path.Combine(dir, "sub");

            Assert.Equal(Path.Combine(other, "clip.mp4"), RenderOutputPath.Resolve(Path.Combine(other, "clip.mp4"), dir, out _));
            Assert.Equal(Path.Combine(other, "clip.mp4"), RenderOutputPath.Resolve(Path.Combine(other, "clip"), dir, out _));
            // a name that already ends in another extension keeps it and becomes an mp4 as well:
            // the renderer writes mp4 whatever the box says.
            Assert.Equal(Path.Combine(other, "clip.mkv.mp4"), RenderOutputPath.Resolve(Path.Combine(other, "clip.mkv"), dir, out _));
        }

        [Fact]
        public void Surrounding_whitespace_is_not_part_of_the_name()
        {
            var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

            Assert.Equal(Path.Combine(dir, "clip.mp4"), RenderOutputPath.Resolve("  clip.mp4  ", dir, out _));
        }

        [Fact]
        public void An_empty_or_impossible_name_is_refused_with_a_reason()
        {
            var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

            Assert.Null(RenderOutputPath.Resolve("", dir, out var empty));
            Assert.False(String.IsNullOrEmpty(empty));

            Assert.Null(RenderOutputPath.Resolve("   ", dir, out var blank));
            Assert.False(String.IsNullOrEmpty(blank));

            // the invalid characters are the platform's own list, so this is one that is invalid
            // everywhere the app runs.
            Assert.Null(RenderOutputPath.Resolve("cli\0p.mp4", dir, out var invalid));
            Assert.False(String.IsNullOrEmpty(invalid));
        }

        [Fact]
        public void A_trailing_separator_names_no_file()
        {
            var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

            Assert.Null(RenderOutputPath.Resolve(dir + Path.DirectorySeparatorChar, dir, out var problem));
            Assert.False(String.IsNullOrEmpty(problem));
        }

        [Fact]
        public void The_toast_says_which_after_render_actions_ran()
        {
            Assert.Equal("Video saved", RenderAfterActions.DescribeOutcome(copied: false, revealed: false));
            Assert.Equal("Copied to clipboard", RenderAfterActions.DescribeOutcome(copied: true, revealed: false));
            Assert.Equal("Video saved · shown in folder", RenderAfterActions.DescribeOutcome(copied: false, revealed: true));
            Assert.Equal("Copied to clipboard · shown in folder", RenderAfterActions.DescribeOutcome(copied: true, revealed: true));
        }

        [Fact]
        public void The_size_row_shows_what_the_encoder_will_be_opened_at()
        {
            // the dialog's caption and the encoder read the same function, so this is the number
            // that ends up in the file (even dimensions, aspect preserved, never upscaled).
            Assert.Equal((1920, 1080), RenderJob.CapSize(1920, 1080, 0));
            Assert.Equal((1280, 720), RenderJob.CapSize(1920, 1080, 720));
            Assert.Equal((1280, 720), RenderJob.CapSize(1280, 720, 1080));
            Assert.Equal((766, 720), RenderJob.CapSize(1240, 1166, 720));
        }
    }
}
