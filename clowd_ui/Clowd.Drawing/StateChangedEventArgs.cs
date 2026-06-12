using System;
using System.Xml.Linq;

namespace Clowd.Drawing
{
    public class StateChangedEventArgs : EventArgs
    {
        public XElement State { get; }

        public StateChangedEventArgs(XElement state)
        {
            State = state;
        }
    }
}
