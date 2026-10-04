using Avalonia;
using Avalonia.Input;
using Clowd.Drawing.Tools;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>The brush keeps a pen's hover frames (zero pressure) out of a stroke's coalesced
    /// samples, and only a pen's, and only when the pen reports pressure at all.</summary>
    public class ToolBrushHoverSampleTests
    {
        private static PointerPoint Sample(float pressure) =>
            new PointerPoint(null, new Point(10, 10),
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other, 0, pressure, 0, 0));

        [Fact]
        public void PenSampleWithoutPressure_WhileThePenPresses_IsHover()
        {
            Assert.True(ToolBrush.IsHoverSample(Sample(0), PointerType.Pen, 0.4f));
        }

        [Fact]
        public void PenSampleWithPressure_IsContact()
        {
            Assert.False(ToolBrush.IsHoverSample(Sample(0.1f), PointerType.Pen, 0.4f));
        }

        [Fact]
        public void PenWithoutAPressureSensor_KeepsEverySample()
        {
            Assert.False(ToolBrush.IsHoverSample(Sample(0), PointerType.Pen, 0));
        }

        [Theory]
        [InlineData(PointerType.Mouse)]
        [InlineData(PointerType.Touch)]
        public void NonPenSamples_AreAlwaysContact(PointerType type)
        {
            Assert.False(ToolBrush.IsHoverSample(Sample(0), type, 0.5f));
        }
    }
}
