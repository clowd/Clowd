using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DrawToolsLib.Graphics;


namespace DrawToolsLib
{
    /// <summary>
    /// Helper class used for XML serialization.
    /// Contains array of SerializedGraphicsBase instances.
    /// </summary>

    [Serializable, XmlRoot("Graphics")]
    public class SerializationHelper
    {
        [XmlArrayItem(typeof(GraphicEllipse)),
         XmlArrayItem(typeof(GraphicLine)),
         XmlArrayItem(typeof(GraphicArrow)),
         XmlArrayItem(typeof(GraphicImage)),
         XmlArrayItem(typeof(GraphicPolyLine)),
         XmlArrayItem(typeof(GraphicRectangle)),
         XmlArrayItem(typeof(GraphicText))]
        public GraphicBase[] Graphics { get; set; }

        [XmlIgnore]
        public Rect ContentBounds
        {
            get { return new Rect(Left, Top, Width, Height); }
            set
            {
                Left = value.Left;
                Top = value.Top;
                Width = value.Width;
                Height = value.Height;
            }
        }

        [XmlAttribute]
        public double Left { get; set; }
        [XmlAttribute]
        public double Top { get; set; }
        [XmlAttribute]
        public double Width { get; set; }
        [XmlAttribute]
        public double Height { get; set; }

        // Default constructor is XML serialization requirement.
        public SerializationHelper()
        {
        }

        public SerializationHelper(IEnumerable<GraphicBase> collection, Rect contentBounds)
        {
            if (collection == null)
                throw new ArgumentNullException(nameof(collection));

            Graphics = collection.ToArray();
            ContentBounds = contentBounds;

        }
    }
}
