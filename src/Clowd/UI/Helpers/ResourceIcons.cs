using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Clowd.UI.Helpers
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

    static class ResourceIcons
    {
        public static System.Windows.Shapes.Path GetIconElement(ResourceIcon icon)
        {
            return (System.Windows.Shapes.Path)Application.Current.Resources[icon.ToString()];
        }
    }
}
