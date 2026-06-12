using System;
using System.Diagnostics;
using System.Threading;

namespace Clowd
{
    public interface ITasksView
    {
        ITasksViewItem CreateTask(string name);
    }

    public interface ITasksViewItem
    {
        string Name { get; }
        string Status { get; }
        CancellationToken CancelToken { get; }

        void Show();
        void Hide();
        void SetCancelled();
        void SetStatus(string status);
        void SetCompleted(string uploadUrl);
        void SetError(Exception ex);
        void SetProgress(long completed, long total, bool isBytes);
    }
}

namespace Clowd.UI
{
    // no-op implementation: the WPF version showed Windows toast notifications for upload progress,
    // which are out of scope for this migration (upload providers do not ship in this build).
    public class TasksViewManager : ITasksView
    {
        public ITasksViewItem CreateTask(string name)
        {
            return new TasksViewItem(name);
        }

        private sealed class TasksViewItem : ITasksViewItem
        {
            public string Name { get; }

            public string Status { get; private set; }

            public CancellationToken CancelToken => _source.Token;

            private readonly CancellationTokenSource _source = new CancellationTokenSource();

            public TasksViewItem(string name)
            {
                Name = name;
            }

            public void Show()
            { }

            public void Hide()
            { }

            public void SetCancelled()
            {
                _source.Cancel();
            }

            public void SetStatus(string status)
            {
                Status = status;
            }

            public void SetCompleted(string uploadUrl)
            { }

            public void SetError(Exception ex)
            {
                Debug.WriteLine($"Task '{Name}' failed: {ex}");
            }

            public void SetProgress(long completed, long total, bool isBytes)
            { }
        }
    }
}
