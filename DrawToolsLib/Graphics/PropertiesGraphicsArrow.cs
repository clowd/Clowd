using System;

namespace DrawToolsLib
{
    [Serializable]
    public class PropertiesGraphicsArrow : PropertiesGraphicsLine
    {
        public PropertiesGraphicsArrow() { }

        public PropertiesGraphicsArrow(GraphicsArrow arrow)
            : base(arrow)
        { }

        public override GraphicsBase CreateGraphics()
        {
            GraphicsBase b = new GraphicsArrow(Start, End, LineWidth, ObjectColor, ActualScale);
            if (this.ID != 0)
            {
                b.Id = this.ID;
                b.IsSelected = this.Selected;
            }
            return b;
        }
    }
}
