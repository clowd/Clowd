using System;
using System.Threading;
using Clowd.Util;

namespace Clowd.UI.Helpers
{
    public class TasksView : ITasksView
    {
        private readonly TaskWindow _window = new TaskWindow();

        public void Notify()
        {
            _window.Dispatcher.Invoke(() =>
            {
                _window.Notify();
            });
        }

        public void Show()
        {
            _window.Dispatcher.Invoke(() =>
            {
                _window.Show();
            });
        }

        public void Hide()
        {
            _window.Dispatcher.Invoke(() =>
            {
                _window.Hide();
            });
        }

        public ITasksViewItem CreateTask(string name)
        {
            return new TasksViewItem(_window, name);
        }
    }

    public class TasksViewItem : ITasksViewItem
    {
        public string Name => _viewItem.PrimaryText;
        public string Status => _viewItem.SecondaryText;
        public CancellationToken CancelToken => _tcs.Token;

        private readonly TaskWindow _window;
        private readonly UploadTaskViewItem _viewItem;
        private readonly CancellationTokenSource _tcs;

        private const int MAX_UNFINISHED_PROGRESS = 95;

        public TasksViewItem(TaskWindow window, string name)
        {
            _window = window;
            _tcs = new CancellationTokenSource();
            _viewItem = new UploadTaskViewItem(name, "Preparing...", _tcs);
        }

        public void SetStatus(string status)
        {
            _window.Dispatcher.Invoke(() =>
            {
                _viewItem.SecondaryText = status;
            });
        }

        public void SetProgress(double percProgress)
        {
            _window.Dispatcher.Invoke(() =>
            {
                _viewItem.ProgressTargetText = "";
                _viewItem.ProgressCurrentText = "";
                _viewItem.Progress = percProgress * 100;
            });
        }

        public void SetProgress(long completedBytes, long totalBytes)
        {
            var progress = completedBytes * 100 / totalBytes;
            if (progress > MAX_UNFINISHED_PROGRESS) progress = MAX_UNFINISHED_PROGRESS; // caller should use SetCompleted() to indicate success.

            var current = completedBytes < totalBytes ? completedBytes : totalBytes;

            var decimals = totalBytes > 1000000 ? 1 : 0;

            var currentText = current.ToPrettySizeString(decimals);
            var targetText = totalBytes.ToPrettySizeString(decimals);

            _window.Dispatcher.Invoke(() =>
            {
                _viewItem.ProgressTargetText = targetText;
                _viewItem.ProgressCurrentText = currentText;
                _viewItem.Progress = progress;
            });
        }

        public void SetCompleted(string uploadUrl)
        {
            _window.Dispatcher.Invoke(() =>
            {
                _viewItem.Status = TaskViewItem.TaskStatus.Complete;
                _viewItem.UploadURL = uploadUrl;
                _viewItem.SecondaryText = "Complete";
                _viewItem.Progress = 100;
                _viewItem.ProgressCurrentText = _viewItem.ProgressTargetText;
                _window.Notify();
            });
        }

        public void SetError(Exception ex)
        {
            _window.Dispatcher.Invoke(() =>
            {
                _viewItem.Progress = 100;
                _viewItem.Status = TaskViewItem.TaskStatus.Error;
                _viewItem.SecondaryText = ex.Message;
            });
        }

        public void Show()
        {
            _window.Dispatcher.Invoke(() =>
            {
                _window.AddTask(_viewItem);
                _window.Show();
            });
        }

        public void Hide()
        {
            _window.Dispatcher.Invoke(() =>
            {
                _window.RemoveTask(_viewItem);
                if (_window.TaskList.Count == 0)
                    _window.Hide();
            });
        }
    }
}
