using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

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
        //CanvasZoom = 1 << 6,
    }

    //[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    //public class ToolDescAttribute : Attribute
    //{
    //    public string Name { get; }

    //    public ToolType ToolType { get; }

    //    public Type ObjectType { get; set; }

    //    public Skill Skills { get; set; }

    //    public ToolDescAttribute(string name, ToolType type)
    //    {
    //        Name = name;
    //        ToolType = type;
    //    }
    //}

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
