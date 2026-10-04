using Clowd.UI.DrawOnScreen;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The cross-screen undo order: one Undo button over one canvas history per screen.
    /// </summary>
    public class DrawHistoryTests
    {
        private sealed class FakeTarget : IDrawUndoTarget
        {
            public bool CanUndo { get; set; } = true;
            public int Undos { get; private set; }
            public System.Action OnUndo { get; set; }

            public void Undo()
            {
                Undos++;
                OnUndo?.Invoke();
            }
        }

        [Fact]
        public void Interleaved_targets_undo_last_in_first_out()
        {
            var h = new DrawHistory();
            var a = new FakeTarget();
            var b = new FakeTarget();
            h.Record(a);
            h.Record(b);
            h.Record(a);

            Assert.True(h.Undo());
            Assert.Equal((1, 0), (a.Undos, b.Undos));
            Assert.True(h.Undo());
            Assert.Equal((1, 1), (a.Undos, b.Undos));
            Assert.True(h.Undo());
            Assert.Equal((2, 1), (a.Undos, b.Undos));
            Assert.False(h.CanUndo);
            Assert.False(h.Undo());
        }

        [Fact]
        public void A_group_undoes_every_member_once()
        {
            var h = new DrawHistory();
            var a = new FakeTarget();
            var b = new FakeTarget();
            h.Record(a);
            h.BeginGroup();
            h.Record(a);
            h.Record(b);
            h.Record(a);
            h.EndGroup();

            Assert.True(h.Undo());
            Assert.Equal((1, 1), (a.Undos, b.Undos));
            Assert.True(h.CanUndo);
            Assert.True(h.Undo());
            Assert.Equal((2, 1), (a.Undos, b.Undos));
            Assert.False(h.CanUndo);
        }

        [Fact]
        public void An_empty_group_pushes_nothing()
        {
            var h = new DrawHistory();
            h.BeginGroup();
            h.EndGroup();
            Assert.False(h.CanUndo);
        }

        [Fact]
        public void An_entry_that_cannot_undo_is_skipped()
        {
            var h = new DrawHistory();
            var a = new FakeTarget();
            var b = new FakeTarget { CanUndo = false };
            h.Record(a);
            h.Record(b);

            Assert.True(h.Undo());
            Assert.Equal(1, a.Undos);
            Assert.Equal(0, b.Undos);
            Assert.False(h.CanUndo);
        }

        [Fact]
        public void Nothing_undoable_returns_false_and_empties_the_history()
        {
            var h = new DrawHistory();
            h.Record(new FakeTarget { CanUndo = false });
            Assert.False(h.Undo());
            Assert.False(h.CanUndo);
        }

        [Fact]
        public void Forget_drops_the_target_and_emptied_entries()
        {
            var h = new DrawHistory();
            var a = new FakeTarget();
            var b = new FakeTarget();
            h.Record(a);
            h.BeginGroup();
            h.Record(a);
            h.Record(b);
            h.EndGroup();
            h.Record(b);

            h.Forget(b);

            // the trailing b entry is gone, the group is left with just a, the first a remains
            Assert.True(h.Undo());
            Assert.Equal((1, 0), (a.Undos, b.Undos));
            Assert.True(h.Undo());
            Assert.Equal((2, 0), (a.Undos, b.Undos));
            Assert.False(h.CanUndo);
        }

        [Fact]
        public void Records_made_while_undoing_are_ignored()
        {
            var h = new DrawHistory();
            var a = new FakeTarget();
            a.OnUndo = () => h.Record(a);
            h.Record(a);

            Assert.True(h.Undo());
            Assert.False(h.CanUndo);
        }

        [Fact]
        public void Clear_empties_the_history()
        {
            var h = new DrawHistory();
            h.Record(new FakeTarget());
            h.Clear();
            Assert.False(h.CanUndo);
        }

        [Fact]
        public void Changed_fires_on_every_mutation()
        {
            var h = new DrawHistory();
            var fired = 0;
            h.Changed += (_, _) => fired++;
            var a = new FakeTarget();

            h.Record(a);
            Assert.Equal(1, fired);

            h.BeginGroup();
            h.Record(a);
            h.EndGroup();
            Assert.Equal(2, fired);

            h.Undo();
            Assert.Equal(3, fired);

            h.Forget(a);
            Assert.Equal(4, fired);

            h.Clear();
            Assert.Equal(5, fired);
        }
    }
}
