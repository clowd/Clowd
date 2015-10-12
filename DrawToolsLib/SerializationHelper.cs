using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;



namespace DrawToolsLib
{
    /// <summary>
    /// Helper class used for XML serialization.
    /// Contains array of SerializedGraphicsBase instances.
    /// </summary>
    
    [XmlRoot("Graphics")]
    public class SerializationHelper
    {
        PropertiesGraphicsBase[] graphics;

        /// <summary>
        /// Default constructor is XML serialization requirement.
        /// </summary>
        public SerializationHelper()
        {

        }

        /// <summary>
        /// This constructor is used for serialization.
        /// VisualCollection contains Graphics* instances.
        /// Every Graphics instance creates SerializedGraphics*
        /// instance which is added to graphics array.
        /// </summary>
        public SerializationHelper(VisualCollection collection)
        {
            if ( collection == null )
            {
                throw new ArgumentNullException("collection");
            }

            graphics = new PropertiesGraphicsBase[collection.Count];

            int i = 0;

            foreach (GraphicsBase g in collection)
            {
                graphics[i++] = g.CreateSerializedObject();
            }
        }

        /// <summary>
        /// When class is serialized, graphics array is filled in the constructor
        /// and saved to XML file.
        /// When class is deserialized, graphics array is loaded from XML file
        /// and then used by called to create VisualCollection.
        /// </summary>

        [XmlArrayItem(typeof(PropertiesGraphicsEllipse)),
         XmlArrayItem(typeof(PropertiesGraphicsLine)),
         XmlArrayItem(typeof(PropertiesGraphicsPolyLine)),
         XmlArrayItem(typeof(PropertiesGraphicsRectangle)),
         XmlArrayItem(typeof(PropertiesGraphicsText))]
        public PropertiesGraphicsBase[] Graphics
        {
            get { return graphics; }
            set { graphics = value; }
        }
    }
}
