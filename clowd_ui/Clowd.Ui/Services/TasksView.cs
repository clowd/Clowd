using System;
using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Threading;

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
    public enum UploadTaskState
    {
        InProgress,
        Complete,
        Error,
        Canceled,
    }

    /// <summary>
    /// Replaces the WPF toast notifications with the bottom-right <see cref="TaskWindow"/>
    /// overlay (modeled on the original Clowd TaskWindow). Items are added as uploads start;
    /// a finished upload re-opens the overlay if it was minimized.
    /// </summary>
    public class TasksViewManager : ITasksView
    {
        public ObservableCollection<UploadTaskViewModel> Tasks { get; } = new();

        private TaskWindow _window;

        public ITasksViewItem CreateTask(string name)
        {
            return new UploadTaskViewModel(this, name);
        }

        /// <summary>Opens (or re-opens) the overlay window, e.g. from the tray menu.</summary>
        public void ShowOverlay()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _window ??= new TaskWindow(this);
                _window.ShowOverlay();
            });
        }

        public void MinimizeOverlay()
        {
            _window?.Hide();
        }

        internal void AddTask(UploadTaskViewModel item)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!Tasks.Contains(item))
                    Tasks.Add(item);
                ShowOverlay();
            });
        }

        internal void RemoveTask(UploadTaskViewModel item)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Tasks.Remove(item);
                if (Tasks.Count == 0)
                    _window?.Hide();
            });
        }

        /// <summary>Called when a task reaches a terminal state — re-opens the overlay so the
        /// result (Copy to Clipboard / error) is visible even if the user minimized it.</summary>
        internal void NotifyTaskFinished(UploadTaskViewModel item)
        {
            ShowOverlay();
        }
    }

    public sealed class UploadTaskViewModel : SimpleNotifyObject, ITasksViewItem
    {
        public string Name { get; }

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        public double Progress
        {
            get => _progress;
            private set => Set(ref _progress, value);
        }

        public UploadTaskState State
        {
            get => _state;
            private set => Set(ref _state, value, nameof(State),
                nameof(IsInProgress), nameof(IsFinished), nameof(CanCopy), nameof(IsError));
        }

        public string Url
        {
            get => _url;
            private set => Set(ref _url, value, nameof(Url), nameof(CanCopy));
        }

        public bool IsInProgress => State == UploadTaskState.InProgress;
        public bool IsFinished => State != UploadTaskState.InProgress;
        public bool IsError => State == UploadTaskState.Error;
        public bool CanCopy => State == UploadTaskState.Complete && !String.IsNullOrEmpty(Url);

        public CancellationToken CancelToken => _source.Token;

        private readonly TasksViewManager _manager;
        private readonly CancellationTokenSource _source = new();
        private string _status;
        private double _progress;
        private UploadTaskState _state = UploadTaskState.InProgress;
        private string _url;

        public UploadTaskViewModel(TasksViewManager manager, string name)
        {
            _manager = manager;
            Name = name;
        }

        public void Show() => _manager.AddTask(this);

        public void Hide() => _manager.RemoveTask(this);

        /// <summary>Dismiss-button behavior: cancels while running, removes once finished.</summary>
        public void CancelOrDismiss()
        {
            if (IsInProgress)
                SetCancelled();
            else
                Hide();
        }

        public void SetCancelled()
        {
            _source.Cancel();
            Dispatcher.UIThread.Post(() =>
            {
                if (State == UploadTaskState.InProgress)
                {
                    State = UploadTaskState.Canceled;
                    Status = "Canceled";
                }
            });
        }

        public void SetStatus(string status)
        {
            Dispatcher.UIThread.Post(() => Status = status);
        }

        public void SetCompleted(string uploadUrl)
        {
            Dispatcher.UIThread.Post(() =>
            {
                // some providers do not observe the cancel token mid-transfer; once the user
                // cancels, a late completion/error must not resurrect the item.
                if (State == UploadTaskState.Canceled)
                    return;

                Url = uploadUrl;
                Progress = 100;
                State = UploadTaskState.Complete;
                Status = "Upload complete";
                _manager.NotifyTaskFinished(this);
            });
        }

        public void SetError(Exception ex)
        {
            var message = (ex is AggregateException agg ? agg.GetBaseException() : ex)?.Message ?? "Unknown error";
            Dispatcher.UIThread.Post(() =>
            {
                if (State == UploadTaskState.Canceled)
                    return;

                State = UploadTaskState.Error;
                Status = message;
                _manager.NotifyTaskFinished(this);
            });
        }

        public void SetProgress(long completed, long total, bool isBytes)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (State != UploadTaskState.InProgress)
                    return;

                Progress = total > 0 ? Math.Min(100, completed / (double)total * 100d) : 0;
                Status = isBytes
                    ? $"{PrettyBytes(completed)} / {PrettyBytes(total)}"
                    : $"{completed} / {total}";
            });
        }

        private static string PrettyBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.#} {units[unit]}";
        }
    }
}
