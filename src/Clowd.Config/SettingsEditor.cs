using System.ComponentModel;
using RT.Serialization;

namespace Clowd
{
    public enum ToolType
    {
        None,
        Pointer,
        Rectangle,
        FilledRectangle,
        Ellipse,
        Line,
        Arrow,
        PolyLine,
        Text,
        Count,
        Pixelate,
    };

    public enum FontStyleType
    {
        Normal = 0,
        Oblique = 1,
        Italic = 2,
    }

    namespace Config
    {
        [ClassifyIgnoreIfDefault, ClassifyIgnoreIfEmpty]
        public class SavedToolSettings : SimpleNotifyObject
        {
            public bool AutoColor
            {
                get => _autoColor ?? true;
                set => Set(ref _autoColor, value);
            }

            public ColorOption ObjectColor
            {
                get => _objectColor ?? new ColorOption(255, 0, 0);
                set => Set(ref _objectColor, value);
            }

            public double LineWidth
            {
                get => _lineWidth ?? 2d;
                set => Set(ref _lineWidth, value);
            }

            public string FontFamily
            {
                get => _fontFamily ?? "Segoe UI";
                set => Set(ref _fontFamily, value);
            }

            public double FontSize
            {
                get => _fontSize ?? 12d;
                set => Set(ref _fontSize, value);
            }

            public FontStyleType FontStyle
            {
                get => _fontStyle ?? FontStyleType.Normal;
                set => Set(ref _fontStyle, value);
            }

            public int FontWeight
            {
                get => _fontWeight ?? 400;
                set => Set(ref _fontWeight, value);
            }

            public double BlurRadius
            {
                get => _blurRadius ?? 8d;
                set => Set(ref _blurRadius, value);
            }

            private int? _fontWeight;
            private FontStyleType? _fontStyle;
            private double? _fontSize;
            private string _fontFamily;
            private double? _lineWidth;
            private ColorOption? _objectColor;
            private bool? _autoColor;
            private double? _blurRadius;
        }

        public class SettingsEditor : CategoryBase
        {
            public bool RestoreSessionsOnClowdStart
            {
                get => _restoreSessionsOnClowdStart;
                set => Set(ref _restoreSessionsOnClowdStart, value);
            }

            public ColorOption CanvasBackground
            {
                get => _canvasBackground;
                set => Set(ref _canvasBackground, value);
            }

            [Browsable(false)]
            public int StartupPadding
            {
                get => _startupPadding;
                set => Set(ref _startupPadding, value);
            }

            [DisplayName("Tool preferences")]
            public AutoDictionary<ToolType, SavedToolSettings> Tools
            {
                get => _tools;
                set => Set(ref _tools, value);
            }

            public TimeOption DeleteSessionsAfter
            {
                get => _deleteSessionsAfter;
                set => Set(ref _deleteSessionsAfter, value);
            }

            private ColorOption _canvasBackground = new ColorOption(0, 0, 0, 0);
            private int _startupPadding = 30;
            private TimeOption _deleteSessionsAfter = new TimeOption(30, TimeOptionUnit.Days);
            private AutoDictionary<ToolType, SavedToolSettings> _tools = new AutoDictionary<ToolType, SavedToolSettings>();
            private bool _restoreSessionsOnClowdStart = true;

            public SettingsEditor()
            {
                Subscribe(Tools);
            }
        }
    }
}
