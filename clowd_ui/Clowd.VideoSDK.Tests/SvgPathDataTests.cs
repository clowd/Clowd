using System;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The wallpaper reader's own path tokenizer: the corpus's grammar exactly — implicit
    /// linetos after a moveto (every Big Sur quad), implicit repetition, relative verbs,
    /// scientific notation, sign-separated numbers — and the skeleton equality an animated
    /// <c>d</c> depends on. Anything outside the grammar must be rejected, not approximated.
    /// </summary>
    public class SvgPathDataTests
    {
        [Fact]
        public void Implicit_lineto_after_moveto_builds_a_four_point_contour()
        {
            var (skeleton, numbers) = SvgPathSkeleton.Parse("M 10 10  20 10  20 20  10 20 Z");
            Assert.Equal(5, skeleton.VerbCount);
            Assert.Equal(8, skeleton.NumberCount);
            using var path = skeleton.Build(numbers);
            Assert.Equal(4, path.PointCount);
            Assert.Equal(SKRect.Create(10, 10, 10, 10), path.Bounds);
        }

        [Fact]
        public void Implicit_repetition_of_cubics()
        {
            var (skeleton, numbers) = SvgPathSkeleton.Parse("M0 0C1 1 2 2 3 3 4 4 5 5 6 6Z");
            Assert.Equal(4, skeleton.VerbCount); // M C C Z
            Assert.Equal(14, skeleton.NumberCount);
            using var path = skeleton.Build(numbers);
            Assert.Equal(new SKPoint(6, 6), path.LastPoint);
        }

        [Fact]
        public void Scientific_notation_and_sign_separators()
        {
            var (skeleton, numbers) = SvgPathSkeleton.Parse("M1e-05 7.62939e-06L-1.5-2.5");
            Assert.Equal(2, skeleton.VerbCount);
            Assert.Equal(new[] { 1e-05f, 7.62939e-06f, -1.5f, -2.5f }, numbers);
        }

        [Fact]
        public void Relative_moveto_cubic_and_h_v_track_the_current_point()
        {
            var (skeleton, numbers) = SvgPathSkeleton.Parse("m10 10 c0 0 5 5 10 10 H30 V40 h-5 v-5 z");
            using var path = skeleton.Build(numbers);
            // m -> (10,10); c -> (20,20); H30 -> (30,20); V40 -> (30,40); h-5 -> (25,40); v-5 -> (25,35)
            Assert.Equal(SKRect.Create(10, 10, 20, 30), path.Bounds);
            Assert.Equal(new SKPoint(25, 35), path.GetPoint(path.PointCount - 1));
        }

        [Fact]
        public void Skeleton_equality_ignores_numbers_and_notices_verbs()
        {
            var (a, _) = SvgPathSkeleton.Parse("M62.9 -103.5C88.3 -94.3 120.4 -91.1 133.5 -74.9");
            var (b, _) = SvgPathSkeleton.Parse("M0 0C1 1 2 2 3 3");
            var (c, _) = SvgPathSkeleton.Parse("M0 0L1 1 2 2 3 3");
            Assert.True(a.SameAs(b));
            Assert.False(a.SameAs(c));
            Assert.False(a.SameAs(null));
        }

        [Theory]
        [InlineData("M0 0A5 5 0 0 1 10 10")]   // arcs are outside the grammar
        [InlineData("M0 0Q1 1 2 2")]           // quadratics too
        [InlineData("10 10 L 20 20")]          // data before the first verb
        [InlineData("M0 0Z 5 5")]              // numbers after a closepath
        [InlineData("M0 0L1")]                 // short argument list
        public void Anything_outside_the_grammar_is_rejected(string d)
        {
            Assert.Throws<FormatException>(() => SvgPathSkeleton.Parse(d));
        }

        [Fact]
        public void Number_lists_parse_with_commas_and_whitespace()
        {
            Assert.Equal(new[] { 485.9f, 261.2f }, SvgPathSkeleton.ParseNumbers("485.9 261.2"));
            Assert.Equal(new[] { 900f, 600f }, SvgPathSkeleton.ParseNumbers("900, 600"));
            Assert.Null(SvgPathSkeleton.ParseNumbers("900 abc"));
        }
    }
}
