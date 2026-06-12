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

    /// <summary>
    /// Marks a graphic field as transient (selection/editing state, caches): it is excluded from
    /// graphics serialization (undo snapshots, session file, clipboard) by
    /// <see cref="GraphicsSerializer"/> and resets to its constructed default on deserialization.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class TransientAttribute : Attribute
    { }

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
