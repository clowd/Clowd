using System;

namespace Clowd.PlatformUtil
{
    /// <summary>
    /// Specifies which rounding mode to use when converting from a floating-point world coordinate to an integer screen coordinate.
    /// </summary>
    public enum WorldRoundingMode
    {
        /// <summary>
        /// Round world coordinates using Math.Round(double)
        /// </summary>
        Midpoint = 0,
        /// <summary>
        /// Round world coordinates using Math.Ceiling(double)
        /// </summary>
        Ceiling = 1,
        /// <summary>
        /// Round world coordinates using Math.Floor(double)
        /// </summary>
        Floor = 2,
        /// <summary>
        /// Round up for .75 and above, floor for below.
        /// </summary>
        RoundPreferFloor = 3,
    }

    /// <summary>
    /// A DpiContext is used to translate coordinates from a specified virtual DPI/PPI into typical screen coordinates @ 96 DPI. 
    /// If translating to/from a window, this should be short lived, and re-created each time a conversion needs to be done, since 
    /// the window DPI depends on the monitor in which it's center point is located and can change at any time.
    /// </summary>
    public class DpiContext
    {
        /// <summary>
        /// Gets the amount, in screen units, that a unit will be translated when converting to and from screen coordinates
        /// </summary>
        public int WorldOffsetX { get; }

        /// <summary>
        /// Gets the amount, in screen units, that a unit will be translated when converting to and from screen coordinates
        /// </summary>
        public int WorldOffsetY { get; }

        /// <summary>
        /// Gets the DPI on the X axis. When monitor zoom is 100%, this is 96. 
        /// </summary>
        public int DpiX { get; }

        /// <summary>
        /// Gets the DPI on the Y axis. When monitor zoom is 100%, this is 96. On Windows Desktop, this value is always the same as <see cref="DpiX"/>
        /// </summary>
        public int DpiY { get; }

        public WorldRoundingMode RoundingMode { get; set; }

        /// <summary>
        /// Gets the DPI scale on the X axis. When DPI is 96, <see cref="DpiScaleX"/> is 1. 
        /// </summary>
        public virtual double DpiScaleX => DpiX / 96.0d;

        /// <summary>
        /// Gets the DPI scale on the Y axis. When DPI is 96, <see cref="DpiScaleY"/> is 1. On Windows Desktop, this value is always the same as <see cref="DpiScaleX"/>
        /// </summary>
        public virtual double DpiScaleY => DpiY / 96.0d;

        public DpiContext(int dpiX, int dpiY)
            : this(dpiX, dpiY, 0, 0) { }

        public DpiContext(int dpiX, int dpiY, int offsetX, int offsetY)
            : this(dpiX, dpiY, offsetX, offsetY, WorldRoundingMode.RoundPreferFloor) { }

        public DpiContext(int dpiX, int dpiY, int offsetX, int offsetY, WorldRoundingMode roundingMode)
        {
            DpiX = dpiX;
            DpiY = dpiY;
            WorldOffsetX = offsetX;
            WorldOffsetY = offsetY;
            RoundingMode = roundingMode;
        }

        protected virtual Func<double, double> GetRoundingFn(WorldRoundingMode? roundingMode)
        {
            WorldRoundingMode mode = roundingMode ?? RoundingMode;
            switch (mode)
            {
                case WorldRoundingMode.Midpoint: return Math.Round;
                case WorldRoundingMode.Floor: return Math.Floor;
                case WorldRoundingMode.Ceiling: return Math.Ceiling;
                case WorldRoundingMode.RoundPreferFloor: return (v) => v < 0.75d ? Math.Floor(v) : Math.Ceiling(v);
                default: throw new ArgumentOutOfRangeException(nameof(roundingMode));
            }
        }

        public int ToScreenWH(double worldWH, WorldRoundingMode mode) => (int)GetRoundingFn(mode)(worldWH * DpiScaleX);

        public int ToScreenWH(double worldWH) => (int)GetRoundingFn(null)(worldWH * DpiScaleX);

        public int ToScreenX(double worldX) => ToScreenWH(worldX) + WorldOffsetX;

        public int ToScreenY(double worldY) => ToScreenWH(worldY) + WorldOffsetY;

        public ScreenPoint ToScreenPoint(double worldX, double worldY) => new ScreenPoint(ToScreenX(worldX), ToScreenY(worldY));

        public ScreenPoint ToScreenPoint(LogicalPoint worldPoint) => ToScreenPoint(worldPoint.X, worldPoint.Y);

        public ScreenSize ToScreenSize(double worldW, double worldH) => new ScreenSize(ToScreenWH(worldW), ToScreenWH(worldH));

        public ScreenSize ToScreenSize(LogicalSize worldSize) => ToScreenSize(worldSize.Width, worldSize.Height);

        public ScreenRect ToScreenRect(double worldX, double worldY, double worldW, double worldH) => new ScreenRect(ToScreenX(worldX), ToScreenY(worldY), ToScreenWH(worldW), ToScreenWH(worldH));

        public ScreenRect ToScreenRect(LogicalRect worldRect) => ToScreenRect(worldRect.X, worldRect.Y, worldRect.Width, worldRect.Height);

        public double ToWorldWH(int screenWH) => screenWH / DpiScaleX;

        public double ToWorldX(int screenX) => ToWorldWH(screenX - WorldOffsetX);

        public double ToWorldY(int screenY) => ToWorldWH(screenY - WorldOffsetY);

        public LogicalPoint ToWorldPoint(int screenX, int screenY) => new LogicalPoint(ToWorldX(screenX), ToWorldY(screenY));

        public LogicalPoint ToWorldPoint(ScreenPoint screenPoint) => ToWorldPoint(screenPoint.X, screenPoint.Y);

        public LogicalSize ToWorldSize(int screenW, int screenH) => new LogicalSize(ToWorldWH(screenW), ToWorldWH(screenH));

        public LogicalSize ToWorldSize(ScreenSize screenSize) => ToWorldSize(screenSize.Width, screenSize.Height);

        public LogicalRect ToWorldRect(int screenX, int screenY, int screenW, int screenH) => new LogicalRect(ToWorldX(screenX), ToWorldY(screenY), ToWorldWH(screenW), ToWorldWH(screenH));

        public LogicalRect ToWorldRect(ScreenRect screenRect) => ToWorldRect(screenRect.Left, screenRect.Top, screenRect.Width, screenRect.Height);

        public double Round(double worldWH) => ToWorldWH(ToScreenWH(worldWH));

        public double Round(double worldWH, WorldRoundingMode roundingMode) => ToWorldWH(ToScreenWH(worldWH, roundingMode));

        public LogicalPoint Round(LogicalPoint worldPoint) => ToWorldPoint(ToScreenPoint(worldPoint));

        public LogicalRect Round(LogicalRect worldRect) => ToWorldRect(ToScreenRect(worldRect));

        public LogicalSize Round(LogicalSize worldSize) => ToWorldSize(ToScreenSize(worldSize));

        public override string ToString() => $"DpiX={DpiX} ({DpiScaleX * 100}%), DpiY={DpiY} ({DpiScaleY * 100}%)";
    }
}
