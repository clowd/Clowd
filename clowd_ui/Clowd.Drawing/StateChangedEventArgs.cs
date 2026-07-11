using System;
using System.Text.Json.Nodes;

namespace Clowd.Drawing
{
    public class StateChangedEventArgs : EventArgs
    {
        public JsonObject State { get; }

        /// <summary>
        /// The serialized undo chain for history.json (MIGRATION.md §8.8), attached by the
        /// autosave throttle to every <see cref="DrawingCanvas.StateUpdated"/> raise that carries
        /// a document — the two files move in lockstep. Null on the engine's internal merge
        /// raises (which carry no State either).
        /// </summary>
        public JsonObject History { get; }

        public StateChangedEventArgs(JsonObject state, JsonObject history = null)
        {
            State = state;
            History = history;
        }
    }
}
