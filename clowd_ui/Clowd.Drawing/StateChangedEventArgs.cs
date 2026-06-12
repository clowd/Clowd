using System;
using System.Text.Json.Nodes;

namespace Clowd.Drawing
{
    public class StateChangedEventArgs : EventArgs
    {
        public JsonObject State { get; }

        public StateChangedEventArgs(JsonObject state)
        {
            State = state;
        }
    }
}
