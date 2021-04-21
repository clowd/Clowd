using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DrawToolsLib.Graphics
{
    public interface IActivatableGraphic
    {
        void Activate(DrawingCanvas canvas);
    }
}
