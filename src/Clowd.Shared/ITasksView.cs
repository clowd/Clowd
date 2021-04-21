using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd
{
    public interface ITasksView
    {
        void Show();
        void Hide();
        void Notify();
        ITasksViewItem CreateTask(string name);
    }

    public interface ITasksViewItem
    {
        string Name { get; }
        string Status { get; }
        CancellationToken CancelToken { get; }

        void Show();
        void Hide();
        void SetStatus(string status);
        void SetCompleted(string uploadUrl);
        void SetError(Exception ex);
        void SetProgress(double percProgress);
        void SetProgress(long completedBytes, long totalBytes);
    }
}
