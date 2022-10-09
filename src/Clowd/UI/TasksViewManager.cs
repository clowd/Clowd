using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Clowd.Util;
using Microsoft.Toolkit.Uwp.Notifications;
using NLog;
using Clowd.Clipboard;

namespace Clowd.UI
{
    public class TasksViewManager : ITasksView
    {
        public ITasksViewItem CreateTask(string name)
        {
            return new TasksViewItem(name);
        }
    }

    public class TasksViewItem : ITasksViewItem
    {
        public string Name { get; }

        public string Status { get; private set; }

        public double Progress { get; private set; }

        public string ProgressText { get; private set; }

        public CancellationToken CancelToken => _source.Token;

        private ToastNotification _toast;
        private readonly ToastNotifierCompat _notifier;
        private readonly string _tag;
        private const string _group = "uploads";
        private CancellationTokenSource _source;

        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

        public TasksViewItem(string name)
        {
            Name = name;
            _tag = Guid.NewGuid().ToString();
            _notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            _source = new CancellationTokenSource();
        }

        public void Show()
        {
            lock (_notifier)
            {
                if (_toast == null)
                {
                    var builder = new ToastContentBuilder()
                        .SetToastDuration(ToastDuration.Long)
                        .AddText(Name)
                        .AddButton("Cancel", ToastActivationType.Background, "cancel")
                        .AddVisualChild(new AdaptiveProgressBar()
                        {
                            Value = new BindableProgressBarValue("progressValue"),
                            ValueStringOverride = new BindableString("progressValueString"),
                            Status = new BindableString("progressStatus")
                        });

                    var content = builder.GetToastContent();
                    var toast = new ToastNotification(content.GetXml());
                    toast.Tag = _tag;
                    toast.Group = _group;
                    toast.Data = GetNotificationData();
                    toast.Activated += ToastOnActivated;

                    _toast = toast;
                    _notifier.Show(_toast);
                }
            }
        }

        private void ToastOnActivated(ToastNotification sender, object args)
        {
            if (args is ToastActivatedEventArgs ea)
            {
                if (ea.Arguments == "cancel")
                {
                    SetCancelled();
                }
                else
                {
                    App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);
                    });
                }
            }
        }

        public void Hide()
        {
            lock (_notifier)
            {
                if (_toast != null)
                {
                    _notifier.Hide(_toast);
                    _toast = null;
                }
            }
        }

        public void SetCancelled()
        {
            _source.Cancel();
            Hide();
            new ToastContentBuilder()
                .AddText($"{Name} Cancelled")
                .AddText("The file was cancelled by user and was not uploaded.")
                .Show();
        }

        public void SetStatus(string status)
        {
            Status = status;
            UpdateToast();
        }

        public void SetCompleted(string uploadUrl)
        {
            Hide();
            ClipboardWpf.SetText(uploadUrl);

            new ToastContentBuilder()
                .AddText($"{Name} Uploaded")
                .AddText("The URL has been copied to the clipboard.")
                .Show();
        }

        public void SetError(Exception ex)
        {
            _log.Error(ex, "Failed to upload file.");
            Hide();

            if (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
                ex = agg.InnerException;

            new ToastContentBuilder()
                .SetToastScenario(ToastScenario.Reminder)
                .AddText($"{Name} Failed")
                .AddText("File could not be uploaded.")
                .AddText(ex.Message)
                .Show();
        }

        public void SetProgress(double percProgress)
        {
            Progress = percProgress;
            ProgressText = $"{percProgress}%";
            UpdateToast();
        }

        public void SetProgress(long completed, long total, bool isBytes)
        {
            Progress = completed / (double)total;

            ProgressText = isBytes
                ? $"{PrettySize.Format(completed)} / {PrettySize.Format(total)}"
                : $"{completed} / {total}";

            UpdateToast();
        }

        private void UpdateToast()
        {
            lock (_notifier)
            {
                if (_toast != null)
                {
                    _notifier.Update(GetNotificationData(), _tag, _group);
                }
            }
        }

        private NotificationData GetNotificationData()
        {
            var data = new NotificationData();
            data.SequenceNumber = 0;
            data.Values["progressValue"] = Progress.ToString();
            data.Values["progressValueString"] = ProgressText;
            data.Values["progressStatus"] = Status;
            return data;
        }
    }
}
