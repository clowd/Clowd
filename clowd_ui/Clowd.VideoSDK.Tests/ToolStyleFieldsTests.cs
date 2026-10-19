using System;
using System.Linq;
using Clowd.Config;
using Clowd.Drawing;
using Clowd.UI.Controls;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The style fields shared by the editor's property bar and the settings window's tool
    /// defaults: every setting a tool has must have a field, and every field a settings path.
    /// </summary>
    public class ToolStyleFieldsTests
    {
        // skills that are not a tool style (the pointer's canvas background)
        private const Skill NotStyles = Skill.CanvasBackground | Skill.Crop | Skill.Cursor;

        [Fact]
        public void Every_tool_skill_has_a_field()
        {
            var covered = ToolStyleFields.All.Aggregate(Skill.None, (acc, f) => acc | f.Skill) | Skill.AutoFill; // AutoFill rides in the Fill field

            foreach (ToolType tool in Enum.GetValues(typeof(ToolType)))
            {
                var missing = DrawingCanvas.GetToolSettingSkills(tool) & ~NotStyles & ~covered;
                Assert.True(missing == Skill.None, $"{tool} has {missing} with no ToolStyleFields entry");
            }
        }

        [Fact]
        public void Tools_never_offer_an_angle()
        {
            foreach (ToolType tool in Enum.GetValues(typeof(ToolType)))
                Assert.False(DrawingCanvas.GetToolSettingSkills(tool).HasFlag(Skill.Angle), $"{tool} offers an angle");
        }

        [Fact]
        public void Every_tool_field_value_has_a_settings_property()
        {
            foreach (var field in ToolStyleFields.All.Where(f => f.Skill != Skill.Angle))
            {
                foreach (var value in field.Values)
                {
                    Assert.True(ToolStyleFields.SettingsPaths.TryGetValue(value, out var path), $"{value} has no settings path");
                    Assert.NotNull(typeof(SavedToolSettings).GetProperty(path));
                }
            }
        }

        [Fact]
        public void Every_field_value_has_a_canvas_property()
        {
            foreach (var value in ToolStyleFields.All.SelectMany(f => f.Values))
            {
                Assert.True(ToolStyleFields.CanvasPaths.TryGetValue(value, out var path), $"{value} has no canvas path");
                Assert.NotNull(typeof(DrawingCanvas).GetProperty(path));
            }
        }

        [Fact]
        public void Tool_skills_follow_the_graphic_they_draw()
        {
            Assert.True(DrawingCanvas.GetToolSettingSkills(ToolType.Rectangle).HasFlag(Skill.Radius));
            Assert.True(DrawingCanvas.GetToolSettingSkills(ToolType.Text).HasFlag(Skill.AutoFill));
            Assert.Equal(Skill.BlurRadius | Skill.ObscureMode, DrawingCanvas.GetToolSettingSkills(ToolType.Pixelate));
            Assert.Equal(Skill.None, DrawingCanvas.GetToolSettingSkills(ToolType.Eraser));
        }
    }
}
