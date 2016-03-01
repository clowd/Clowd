using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Serialization;


namespace DrawToolsLib
{
    /// <summary>
    /// Base class for all serialization helper classes.
    /// PropertiesGraphics* class hierarchy contains base class
    /// and class for every non-abstract Graphics* class.
    /// Since Graphics* classes are derived from DrawingVisual, I
    /// cannot serialize them directly.
    /// 
    /// Every PropertiesGraphics* class knows to create instance
    /// of Graphics* class - see CreateGraphics function.
    /// This function is called during deserialization, when 
    /// PropertiesGraphics* class is loaded from XML file.
    /// 
    /// On the other hand, every non-abstract Graphics* class
    /// can create PropertiesGraphics* class: see 
    /// GraphicsBase.CreateSerializedObject function.
    /// It is called during serialization, when every Graphics*
    /// object must create PropertiesGraphics*.
    /// 
    /// 
    /// PropertiesGraphics* classes are also used in UndoManager
    /// as light-weight clones of Graphics* classes.
    /// These classes are also used in DrawingCanvas.GetListOfGraphicObjects
    /// function for client which needs to get all data from DrawingCanvas
    /// directly.
    /// 
    /// </summary>
    [Serializable]
    public abstract class PropertiesGraphicsBase
    {
        [XmlIgnore]
        internal int ID;

        [XmlIgnore]
        internal bool selected;

        [XmlIgnore]
        internal double actualScale;


        public abstract GraphicsBase CreateGraphics();
    }
}
