using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Clowd
{
    enum ResourceIcon
    {
        IconClowd,
        IconPhoto,
        IconVideo,
        IconCopy,
        IconCopySmall,
        IconSave,
        IconSaveSmall,
        IconSearch,
        IconReset,
        IconClose,
        IconPlay,
        IconStop,
        IconSettings,
        IconDrawing,
        IconMicrophoneEnabled,
        IconMicrophoneDisabled,
        IconSpeakerEnabled,
        IconSpeakerDisabled,
        IconToolNone,
        IconToolPointer,
        IconToolRectangle,
        IconToolFilledRectangle,
        IconToolEllipse,
        IconToolLine,
        IconToolArrow,
        IconToolPolyLine,
        IconToolText,
        IconToolPixelate,
        IconToolErase,
        IconUndo,
        IconRedo,
        IconPinned,
        IconCrop,
        IconChevronDown,
        IconHamburgerMore,
    }

    static class AppStyles
    {
        public static Color AccentColor => (Color)FindResource("SystemAccentColor");

        public static Brush AccentBackgroundBrush => (Brush)FindResource("SystemControlBackgroundAccentBrush");
        public static Brush IdealBackgroundBrush => new SolidColorBrush(Color.FromRgb(55, 55, 55));
        public static Brush IdealForegroundBrush => Brushes.White;

        public static Style AudioLevelProgressBarStyle => (Style)FindResource("AudioLevelProgressBarStyle");

        public static System.Windows.Shapes.Path GetIconElement(ResourceIcon icon)
        {
            return (System.Windows.Shapes.Path)FindResource(icon.ToString());
        }

        private static object FindResource(string resourceName)
        {
            return App.Current.FindResource(resourceName);
        }
    }
}
