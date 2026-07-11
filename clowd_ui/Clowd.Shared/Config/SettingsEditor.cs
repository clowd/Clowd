using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;

namespace Clowd.Config
{
    public class SavedToolSettings : SimpleNotifyObject
    {
        public bool AutoColor
        {
            get => _autoColor ?? true;
            set => Set(ref _autoColor, value);
        }

        public Color ObjectColor
        {
            get => _objectColor ?? Colors.Red;
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

        public FontStyle FontStyle
        {
            get => _fontStyle ?? FontStyle.Normal;
            set => Set(ref _fontStyle, value);
        }

        public FontWeight FontWeight
        {
            get => _fontWeight ?? FontWeight.Normal;
            set => Set(ref _fontWeight, value);
        }

        public FontStretch FontStretch
        {
            get => _fontStretch ?? FontStretch.Normal;
            set => Set(ref _fontStretch, value);
        }

        public double BlurRadius
        {
            get => _blurRadius ?? 8d;
            set => Set(ref _blurRadius, value);
        }

        private FontStretch? _fontStretch;
        private FontWeight? _fontWeight;
        private FontStyle? _fontStyle;
        private double? _fontSize;
        private string _fontFamily;
        private double? _lineWidth;
        private Color? _objectColor;
        private bool? _autoColor;
        private double? _blurRadius;
    }

    public class SettingsEditor : SimpleNotifyObject
    {
        [DisplayName("Restore sessions on startup")]
        [Description("Reopen the editor sessions that were still open when Clowd last exited")]
        public bool RestoreSessionsOnClowdStart
        {
            get => _restoreSessionsOnClowdStart;
            set => Set(ref _restoreSessionsOnClowdStart, value);
        }

        [DisplayName("Canvas background")]
        [Description("Color drawn behind transparent areas of the image in the editor")]
        public Color CanvasBackground
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
        [Description("Every drawing tool remembers the color, line width and font last used with it")]
        public Dictionary<ToolType, SavedToolSettings> Tools
        {
            get => _tools;
            set => Set(ref _tools, value);
        }

        [DisplayName("Delete sessions after")]
        [Description("Captures older than this are removed from Recent sessions automatically")]
        public TimeOption DeleteSessionsAfter
        {
            get => _deleteSessionsAfter;
            set => Set(ref _deleteSessionsAfter, value);
        }

        /// <summary>Lazily creates the per-tool settings entry (replaces the old AutoDictionary
        /// indexer behaviour).</summary>
        public SavedToolSettings GetToolSettings(ToolType tool)
        {
            if (!_tools.TryGetValue(tool, out var settings) || settings == null)
                _tools[tool] = settings = new SavedToolSettings();
            return settings;
        }

        private Color _canvasBackground = Colors.Transparent;
        private int _startupPadding = 30;
        private TimeOption _deleteSessionsAfter = new TimeOption(30, TimeOptionUnit.Days);
        private Dictionary<ToolType, SavedToolSettings> _tools = new Dictionary<ToolType, SavedToolSettings>();
        private bool _restoreSessionsOnClowdStart = true;
    }
}
