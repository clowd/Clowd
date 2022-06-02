using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Clowd.UI.Controls
{
    public class FadingScrollViewer : ScrollViewer
    {
        private const string PART_SCROLL_PRESENTER_CONTAINER_NAME = "PART_ScrollContentPresenterContainer";

        public double FadedEdgeThickness { get; set; }
        public double FadedEdgeFalloffSpeed { get; set; }
        public double FadedEdgeOpacity { get; set; }

        private BlurEffect InnerFadedBorderEffect { get; set; }
        private Border InnerFadedBorder { get; set; }
        private Border OuterFadedBorder { get; set; }


        public FadingScrollViewer()
        {
            this.FadedEdgeThickness = 20;
            this.FadedEdgeFalloffSpeed = 4.0;
            this.FadedEdgeOpacity = 0.0;

            this.ScrollChanged += FadingScrollViewer_ScrollChanged;
            this.SizeChanged += FadingScrollViewer_SizeChanged;
        }


        private void FadingScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (this.InnerFadedBorder == null)
                return;

            var topOffset = CalculateNewMarginBasedOnOffsetFromEdge(this.VerticalOffset);
            var bottomOffset = CalculateNewMarginBasedOnOffsetFromEdge(this.ScrollableHeight - this.VerticalOffset);
            var leftOffset = CalculateNewMarginBasedOnOffsetFromEdge(this.HorizontalOffset);
            var rightOffset = CalculateNewMarginBasedOnOffsetFromEdge(this.ScrollableWidth - this.HorizontalOffset);

            this.InnerFadedBorder.Margin = new Thickness(leftOffset, topOffset, rightOffset, bottomOffset);
        }


        private double CalculateNewMarginBasedOnOffsetFromEdge(double edgeOffset)
        {
            var innerFadedBorderBaseMarginThickness = this.FadedEdgeThickness / 2.0;
            var calculatedOffset = (innerFadedBorderBaseMarginThickness) - (1.5 * (this.FadedEdgeThickness - (edgeOffset / this.FadedEdgeFalloffSpeed)));

            return Math.Min(innerFadedBorderBaseMarginThickness, calculatedOffset);
        }


        private void FadingScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (this.OuterFadedBorder == null || this.InnerFadedBorder == null || this.InnerFadedBorderEffect == null)
                return;

            this.OuterFadedBorder.Width = e.NewSize.Width;
            this.OuterFadedBorder.Height = e.NewSize.Height;

            double innerFadedBorderBaseMarginThickness = this.FadedEdgeThickness / 2.0;
            this.InnerFadedBorder.Margin = new Thickness(innerFadedBorderBaseMarginThickness);
            this.InnerFadedBorderEffect.Radius = this.FadedEdgeThickness;
        }


        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            BuildInnerFadedBorderEffectForOpacityMask();
            BuildInnerFadedBorderForOpacityMask();
            BuildOuterFadedBorderForOpacityMask();
            SetOpacityMaskOfScrollContainer();
        }


        private void BuildInnerFadedBorderEffectForOpacityMask()
        {
            this.InnerFadedBorderEffect = new BlurEffect()
            {
                RenderingBias = RenderingBias.Performance,
            };
        }


        private void BuildInnerFadedBorderForOpacityMask()
        {
            this.InnerFadedBorder = new Border()
            {
                Background = Brushes.Black,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                Effect = this.InnerFadedBorderEffect,
            };
        }


        private void BuildOuterFadedBorderForOpacityMask()
        {
            byte fadedEdgeByteOpacity = (byte)(this.FadedEdgeOpacity * 255);

            this.OuterFadedBorder = new Border()
            {
                Background = new SolidColorBrush(Color.FromArgb(fadedEdgeByteOpacity, 0, 0, 0)),
                ClipToBounds = true,
                Child = this.InnerFadedBorder,
            };
        }


        private void SetOpacityMaskOfScrollContainer()
        {
            var opacityMaskBrush = new VisualBrush()
            {
                Visual = this.OuterFadedBorder
            };

            var scrollContentPresentationContainer = this.Template.FindName(PART_SCROLL_PRESENTER_CONTAINER_NAME, this) as UIElement;

            if (scrollContentPresentationContainer == null)
                return;

            scrollContentPresentationContainer.OpacityMask = opacityMaskBrush;
        }
    }
}
