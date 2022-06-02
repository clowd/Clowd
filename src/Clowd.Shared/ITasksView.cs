using System;
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
        void SetProgress(double percProgress);
        void SetProgress(long completedBytes, long totalBytes);
    }
}
