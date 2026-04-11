using System;

namespace Clowd.Drawing
{
    [Flags]
    public enum Skill
    {
        None = 0,
        Color = 1 << 0,
        AutoColor = 1 << 1,
        Stroke = 1 << 2,
        Font = 1 << 3,
        Angle = 1 << 4,
        CanvasBackground = 1 << 5,
        Crop = 1 << 6,
        Cursor = 1 << 7,
        BlurRadius = 1 << 8,
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class GraphicDescAttribute : Attribute
    {
        public string Name { get; }

        public Skill Skills { get; set; }

        public GraphicDescAttribute(string name)
        {
            Name = name;
        }
    }
}
