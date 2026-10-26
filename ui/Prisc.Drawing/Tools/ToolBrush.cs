using System;
using Avalonia;
using Avalonia.Input;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    /// <summary>
    /// The brush: press to start a stroke, every pointer sample goes into it as it arrives, and
    /// the release commits it as one step and reverts to the pointer like the other one-shot
    /// drawing tools. No smoothing happens here — the look of the stroke is entirely
    /// <see cref="Ink.FreehandStroke"/>'s business, so the samples are stored raw with their
    /// timing and the stroke's tip is always the real cursor. Between two events the platform's
    /// coalesced points (GetIntermediatePoints) are read too, so a fast flick keeps its shape
    /// instead of becoming a polygon of event positions.
    /// </summary>
    internal class ToolBrush : ToolBase
    {
        /// <summary>Samples closer than this (screen px) to the previous one are dropped.</summary>
        internal const double DedupeDistance = 0.5;

        /// <summary>The longest gap (ms) one event may add to the stroke's clock: a hitch or a
        /// context menu must not turn into one two-second sample. The algorithm clamps the same
        /// way; this keeps the stored times honest.</summary>
        internal const double MaxEventGapMs = 50;

        private GraphicBrush _stroke;
        private ulong _lastTimestamp;  // the previous event, in the pointer's clock
        private double _elapsedMs;     // the stroke's own clock (sum of clamped gaps)

        public ToolBrush() : base(() => HelperFunctions.DefaultCursor, SnapMode.None)
        { }

        /// <summary>The simulated pressure the cursor ring is sized at. A click leaves a dot at 0.5
        /// (1·size across) but an unhurried stroke settles nearer 1 (up to 1.5·size), so the ring
        /// shows the width of an ordinary stroke (1.25·size) rather than the click's.</summary>
        internal const double CursorPressure = 0.75;

        // a ring the width of an ordinary stroke; DrawingCanvas re-applies it when the stroke
        // width or the zoom changes (one canvas unit is ContentScale device pixels)
        public override void SetCursor(DrawingCanvas canvas)
        {
            double size = Math.Max(1, GraphicBrush.SizePerLineWidth * canvas.LineWidth);
            canvas.Cursor = BrushCursor.Get(2 * Ink.FreehandStroke.Radius(size, CursorPressure) * canvas.ContentScale);
        }

        /// <summary>The stroke a press starts, at <paramref name="origin"/>.</summary>
        protected virtual GraphicBrush CreateStroke(DrawingCanvas canvas, Point origin) => new GraphicBrush(canvas, origin);

        public override void OnMouseDown(DrawingCanvas canvas, PointerState s, int clickCount)
        {
            if (!s.LeftPressed)
                return;

            canvas.CaptureMouse(s.Pointer);
            canvas.UnselectAll();

            // unselected while drawing so no selection border follows the tip; the release selects it
            _stroke = CreateStroke(canvas, s.Position);
            _lastTimestamp = s.Timestamp;
            _elapsedMs = 0;
            canvas.GraphicsList.Add(_stroke);
        }

        public override void OnMouseMove(DrawingCanvas canvas, PointerState s)
        {
            // a synthetic Shift replay carries no new input sample (and Shift means nothing here)
            if (_stroke == null || s.Pointer == null)
                return;

            double dedupe = DedupeDistance * canvas.CanvasUiElementScale.DpiScaleX;
            double gap = Math.Clamp(s.Timestamp > _lastTimestamp ? s.Timestamp - _lastTimestamp : 0, 1, MaxEventGapMs);

            // one raise per event however many samples it carries (see GraphicBrush.AddSample)
            var points = s.Args?.GetIntermediatePoints(canvas);
            int count = points?.Count ?? 0;
            bool added;
            if (count <= 1)
            {
                added = _stroke.AddSample(s.Position, _elapsedMs + gap, dedupe, notify: false);
            }
            else
            {
                // the coalesced points have no times of their own: spread them evenly over the gap
                added = false;
                for (int j = 0; j < count; j++)
                    added |= _stroke.AddSample(points[j].Position, _elapsedMs + gap * (j + 1) / count, dedupe, notify: false);

                var tail = points[count - 1].Position;
                if (Math.Abs(tail.X - s.Position.X) > 0.01 || Math.Abs(tail.Y - s.Position.Y) > 0.01)
                    added |= _stroke.AddSample(s.Position, _elapsedMs + gap, dedupe, notify: false);
            }

            if (added)
                _stroke.NotifySamplesChanged();

            _elapsedMs += gap;
            _lastTimestamp = s.Timestamp;
        }

        // ToolBase.OnMouseUp replays the final move with the real pointer (the release position
        // becomes the last sample), releases capture and reverts to the pointer tool
        protected override void OnMouseUpImpl(DrawingCanvas canvas)
        {
            if (_stroke == null)
                return;

            _stroke.EndStroke();
            _stroke.IsSelected = true;
            canvas.AddCommandToHistory(false);
            _stroke = null;
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            if (_stroke != null)
            {
                canvas.GraphicsList.Remove(_stroke);
                _stroke = null;
            }
        }
    }
}
