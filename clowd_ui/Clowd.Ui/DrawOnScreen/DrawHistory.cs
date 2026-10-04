using System;
using System.Collections.Generic;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>Something with its own undo stack that <see cref="DrawHistory"/> can step back;
    /// in practice one screen's drawing canvas.</summary>
    public interface IDrawUndoTarget
    {
        bool CanUndo { get; }
        void Undo();
    }

    /// <summary>
    /// One undo order across several independent undo stacks. Each screen has its own canvas and so
    /// its own history; this records which canvas each change landed on, so the toolbar's single
    /// Undo steps back the most recent change wherever it was drawn. A group (clear all, which
    /// touches every canvas) undoes as one step. There is no redo.
    /// </summary>
    public sealed class DrawHistory
    {
        private readonly List<IReadOnlyList<IDrawUndoTarget>> _entries = new();
        private List<IDrawUndoTarget> _group;

        // set while Undo replays targets: a canvas that reported its own undo as a change must not
        // push a fresh entry (the canvas does not today, but the history must not depend on that)
        private bool _replaying;

        /// <summary>Raised after every change to the recorded entries.</summary>
        public event EventHandler Changed;

        public bool CanUndo => _entries.Count > 0;

        /// <summary>Records a change on <paramref name="target"/>: its own entry, or a member of the
        /// open group. Ignored while an undo is being replayed.</summary>
        public void Record(IDrawUndoTarget target)
        {
            if (target == null || _replaying)
                return;

            if (_group != null)
            {
                if (!_group.Contains(target))
                    _group.Add(target);
                return;
            }

            _entries.Add(new[] { target });
            OnChanged();
        }

        /// <summary>Opens a group: every <see cref="Record"/> until <see cref="EndGroup"/> joins one entry.</summary>
        public void BeginGroup() => _group ??= new List<IDrawUndoTarget>();

        /// <summary>Closes the group, pushing it only if something was recorded into it.</summary>
        public void EndGroup()
        {
            var group = _group;
            _group = null;
            if (group == null || group.Count == 0)
                return;

            _entries.Add(group);
            OnChanged();
        }

        /// <summary>
        /// Undoes the most recent entry that still has something to undo, on every member of it
        /// that can. Entries none of whose targets can undo any more are discarded on the way.
        /// </summary>
        public bool Undo()
        {
            var undone = false;
            _replaying = true;
            try
            {
                while (!undone && _entries.Count > 0)
                {
                    var entry = _entries[^1];
                    _entries.RemoveAt(_entries.Count - 1);

                    foreach (var target in entry)
                    {
                        if (target.CanUndo)
                        {
                            target.Undo();
                            undone = true;
                        }
                    }
                }
            }
            finally
            {
                _replaying = false;
            }

            OnChanged();
            return undone;
        }

        /// <summary>Removes <paramref name="target"/> from every entry (its screen went away),
        /// dropping entries left empty.</summary>
        public void Forget(IDrawUndoTarget target)
        {
            _group?.Remove(target);
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (!Contains(entry, target))
                    continue;

                var kept = new List<IDrawUndoTarget>(entry.Count);
                foreach (var t in entry)
                {
                    if (!ReferenceEquals(t, target))
                        kept.Add(t);
                }

                if (kept.Count == 0)
                    _entries.RemoveAt(i);
                else
                    _entries[i] = kept;
            }

            OnChanged();
        }

        public void Clear()
        {
            _entries.Clear();
            _group = null;
            OnChanged();
        }

        private static bool Contains(IReadOnlyList<IDrawUndoTarget> entry, IDrawUndoTarget target)
        {
            foreach (var t in entry)
            {
                if (ReferenceEquals(t, target))
                    return true;
            }

            return false;
        }

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
