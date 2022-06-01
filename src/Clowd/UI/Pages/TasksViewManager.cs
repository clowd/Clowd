using System;
using System.Threading;
using System.Threading.Tasks;
using Clowd.Util;

namespace Clowd.UI.Pages
{
    public class TasksViewManager : ITasksView
    {
        public void Show()
        {
        }

        public void Hide()
        {
        }

        public void Notify()
        {
        }

        public ITasksViewItem CreateTask(string name)
        {
            return new TasksViewItem(name);
        }
    }

    public class TasksViewItem : ITasksViewItem
    {
        public string Name { get; }
        
        public string Status { get; private set; }
        
        public CancellationToken CancelToken { get; } = CancellationToken.None;

        public TasksViewItem(string name)
        {
            Name = name;
        }
        
        public void Show()
        {
        }

        public void Hide()
        {
        }

        public void SetStatus(string status)
        {
            Status = status;
        }

        public void SetCompleted(string uploadUrl)
        {
            App.Current.Dispatcher.InvokeAsync(async () =>
            {
                var data = new ClipboardDataObject();
                data.SetText(uploadUrl);
                await data.SetClipboardData();
                App.Current.ShowBallonTip("File Uploaded", "The URL has been copied to the clipboard.");
            });
        }

        public void SetError(Exception ex)
        {
        }

        public void SetProgress(double percProgress)
        {
        }

        public void SetProgress(long completedBytes, long totalBytes)
        {
        }
    }
}
