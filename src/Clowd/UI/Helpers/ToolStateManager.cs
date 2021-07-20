using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Clowd.Config;
using Clowd.UI.Controls;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;

namespace Clowd.UI.Helpers
{
    public class ToolStateManager
    {
        private readonly List<StateCapabilities> _capabilities;
        private static readonly EmptyCapabilities _empty = new EmptyCapabilities();

        public ToolStateManager()
        {
            _capabilities = new List<StateCapabilities>();

            foreach (ToolType e in Enum.GetValues(typeof(ToolType)))
                if (e < ToolType.Max)
                    _capabilities.Add(new ToolStateCapabilities(e));

            _capabilities.Add(new ObjectStateCapabilities<GraphicArrow>());
            _capabilities.Add(new ObjectStateCapabilities<GraphicEllipse>());
            _capabilities.Add(new ObjectStateCapabilities<GraphicFilledRectangle>());
            _capabilities.Add(new ObjectStateCapabilities<GraphicImage>());
            _capabilities.Add(new ObjectStateCapabilities<GraphicLine>());
            _capabilities.Add(new ObjectStateCapabilities<GraphicPolyLine>());
            _capabilities.Add(new ObjectStateCapabilities<GraphicRectangle>());
            _capabilities.Add(new ObjectStateCapabilities<GraphicText>());
        }

        public StateCapabilities GetObjectCapabilities(object obj)
        {
            if (obj == null)
                return Empty();

            return _capabilities.Single(c => c.IsSupported(obj));
        }

        public static StateCapabilities Empty()
        {
            return _empty;
        }
    }


    public abstract class StateCapabilities : INotifyPropertyChanged
    {
        public abstract string Description { get; }
        public abstract string Name { get; }
        public abstract bool HasAutoColor { get; }
        public abstract bool HasColor { get; }
        public abstract bool HasStroke { get; }
        public abstract bool HasFont { get; }
        public abstract bool HasAngle { get; }
        public virtual bool CanCanvasZoom { get; } = true;
        public virtual bool CanChangeCanvasBackground { get; } = true;
        public virtual bool CanStitchAndCrop { get; } = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public abstract void EnterState(ImageEditorPage page, object obj);
        public abstract void ExitState(ImageEditorPage page);
        public abstract bool IsSupported(object obj);
        public abstract bool IsCurrent(object obj);
    }

    public class EmptyCapabilities : StateCapabilities
    {
        public override string Description => "State";

        public override string Name => "Empty";

        public override bool HasAutoColor => true;

        public override bool HasColor => true;

        public override bool HasStroke => true;

        public override bool HasFont => true;

        public override bool HasAngle => true;

        public override void EnterState(ImageEditorPage page, object obj)
        {
        }

        public override void ExitState(ImageEditorPage page)
        {
        }

        public override bool IsCurrent(object obj)
        {
            return false;
        }

        public override bool IsSupported(object obj)
        {
            return true;
        }
    }

    public class ToolStateCapabilities : StateCapabilities
    {
        private readonly ToolType _tool;
        private readonly ToolActionType _action;

        private static DrawingCanvas _fakeCanvas;
        private ImageEditorPage _page;

        public override string Description => "Tool";

        public override string Name => _tool.ToString();

        public override bool HasAutoColor => _tool == ToolType.Text;

        public override bool HasColor => _action == ToolActionType.Object;

        public override bool HasStroke => _action == ToolActionType.Object && _tool != ToolType.FilledRectangle && _tool != ToolType.Text;

        public override bool HasFont => _tool == ToolType.Text;

        public override bool HasAngle => false;

        public override bool CanChangeCanvasBackground => _action == ToolActionType.Cursor;

        public ToolStateCapabilities(ToolType tool)
        {
            _tool = tool;
            _action = _fakeCanvas.GetToolActionType(tool);
        }

        static ToolStateCapabilities()
        {
            _fakeCanvas = new DrawingCanvas();
        }

        public override void EnterState(ImageEditorPage page, object obj)
        {
            _page = page;

            var canvas = page.drawingCanvas;

            var settings = GetToolSettings();

            canvas.LineWidth = settings.LineWidth;

            canvas.ObjectColor = HasAutoColor && settings.TextObjectColorIsAuto
                ? Colors.Transparent
                : settings.ObjectColor;

            canvas.TextFontFamilyName = settings.FontFamily;
            canvas.TextFontSize = settings.FontSize;
            canvas.TextFontStretch = settings.FontStretch;
            canvas.TextFontStyle = settings.FontStyle;
            canvas.TextFontWeight = settings.FontWeight;

            if (HasAutoColor)
            {
                page.chkColorAuto.Click += ChkColorAuto_Click;
                page.chkColorAuto.IsChecked = settings.TextObjectColorIsAuto;
            }
            else
            {
                page.chkColorAuto.IsChecked = false;
            }
        }

        public override void ExitState(ImageEditorPage page)
        {
            if (HasAutoColor)
            {
                page.chkColorAuto.Click -= ChkColorAuto_Click;
            }

            var canvas = page.drawingCanvas;
            var settings = GetToolSettings();

            settings.LineWidth = canvas.LineWidth;

            if (page.chkColorAuto.IsChecked != true)
            {
                settings.ObjectColor = canvas.ObjectColor;
            }

            settings.FontFamily = canvas.TextFontFamilyName;
            settings.FontSize = canvas.TextFontSize;
            settings.FontStretch = canvas.TextFontStretch;
            settings.FontStyle = canvas.TextFontStyle;
            settings.FontWeight = canvas.TextFontWeight;

            _page = null;
        }

        private void ChkColorAuto_Click(object sender, RoutedEventArgs e)
        {
            var settings = GetToolSettings();
            var isAuto = ((CheckBox)e.Source).IsChecked == true;

            settings.TextObjectColorIsAuto = isAuto;

            _page.drawingCanvas.ObjectColor = HasAutoColor && settings.TextObjectColorIsAuto
                ? Colors.Transparent
                : settings.ObjectColor;
        }

        public override bool IsSupported(object obj)
        {
            if (obj is ToolType tt && tt == _tool)
                return true;

            return false;
        }

        private SavedToolSettings GetToolSettings() => ClowdSettings.Current.Editor.Tools[_tool];

        public override bool IsCurrent(object obj)
        {
            if (obj is ToolType toolc)
                return toolc == _tool;

            return false;
        }
    }

    public class ObjectStateCapabilities<TGraphic> : StateCapabilities where TGraphic : GraphicBase
    {
        public override string Description => "Selection";

        public override string Name
        {
            get
            {
                var gtxt = typeof(TGraphic).Name;
                if (gtxt.StartsWith("Graphic"))
                    gtxt = gtxt.Substring(7);
                return gtxt;
            }
        }

        public override bool HasAutoColor => false;

        public override bool HasColor => IsNotOneOf(typeof(GraphicImage));

        public override bool HasStroke => IsNotOneOf(typeof(GraphicImage), typeof(GraphicText), typeof(GraphicFilledRectangle));

        public override bool HasFont => IsOneOf(typeof(GraphicText));

        public override bool HasAngle => typeof(TGraphic).GetProperty(ANGLE_NAME) != null;

        public override bool CanStitchAndCrop => false; // IsOneOf(typeof(GraphicImage));

        public override bool CanChangeCanvasBackground => false;

        public ObjectStateCapabilities()
        {

        }

        private const string ANGLE_NAME = "Angle";
        private object _currentGraphic;

        public override void EnterState(ImageEditorPage page, object obj)
        {
            _currentGraphic = obj;
            var canvas = page.drawingCanvas;

            if (obj is GraphicBase g)
            {
                if (HasColor)
                    canvas.ObjectColor = g.ObjectColor;

                if (HasStroke)
                    canvas.LineWidth = g.LineWidth;

                if (HasFont && obj is GraphicText txt)
                {
                    canvas.TextFontFamilyName = txt.FontName;
                    canvas.TextFontSize = txt.FontSize;
                    canvas.TextFontStretch = txt.FontStretch;
                    canvas.TextFontStyle = txt.FontStyle;
                    canvas.TextFontWeight = txt.FontWeight;
                }

                if (HasAngle)
                {
                    var angleBinding = new Binding(ANGLE_NAME);
                    angleBinding.Source = obj;
                    angleBinding.Mode = BindingMode.TwoWay;
                    angleBinding.Converter = new Converters.AngleConverter();
                    page.textObjectAngle.SetBinding(SpinnerTextBox.TextProperty, angleBinding);

                    var angleResetBinding = new Binding(ANGLE_NAME);
                    angleResetBinding.Source = obj;
                    angleResetBinding.Mode = BindingMode.TwoWay;
                    page.resetObjectAngle.SetBinding(ResetDefaultButton.CurrentValueProperty, angleResetBinding);
                }

                //if (CanStitchAndCrop)
                //{
                //    var croppingBinding = new Binding(nameof(GraphicImage.IsCropping));
                //    croppingBinding.Source = obj;
                //    croppingBinding.Mode = BindingMode.TwoWay;
                //    page.btnCropImage.SetBinding(ToggleButton.IsCheckedProperty, croppingBinding);
                //}
            }
        }

        public override void ExitState(ImageEditorPage page)
        {
            _currentGraphic = null;
        }

        public override bool IsSupported(object obj)
        {
            return obj.GetType() == typeof(TGraphic);
        }

        private bool IsNotOneOf(params Type[] types) => types.All(t => t != typeof(TGraphic));
        private bool IsOneOf(params Type[] types) => types.Any(t => t == typeof(TGraphic));

        public override bool IsCurrent(object obj)
        {
            return obj == _currentGraphic;
        }
    }
}
