using System;
using System.Windows.Media.Effects;

namespace Clowd.UI.Dialogs.ColorPicker
{
    class ColorWheelShader : ShaderEffect
    {
        public ColorWheelShader()
        {
            this.PixelShader = new PixelShader { UriSource = new Uri("pack://application:,,,/UI/Dialogs/ColorPicker/ColorWheelShader.cso", UriKind.Absolute) };
        }
    }
}
