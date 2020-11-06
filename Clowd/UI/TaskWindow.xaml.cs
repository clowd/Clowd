using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Clowd.UI.Helpers;
using Clowd.Util;
using PropertyChanged;

namespace Clowd.UI
{
    [ImplementPropertyChanged]
    [TemplatePart(Type = typeof(Grid), Name = "PART_WindowContent")]
    public partial class TaskWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public static TaskWindow Current { get; private set; }
        public bool IsMinimized { get; private set; }
        public bool Notifing { get; private set; }
        public bool AutoMinimizeEnabled { get; set; } = true;
        public double OverallProgress
        {
            get
            {
                if (TaskList == null || TaskList.Count < 1)
                    return 0;
                var weights = from t in TaskList
                              let ovr = t.OverallWeight
                              let cur = t.Progress / 100 * ovr
                              select new { Overall = ovr, Current = cur };
                double res = weights.Sum(w => w.Current) / weights.Sum(w => w.Overall) * 100;
                return res;
            }
        }
        public ObservableCollection<TaskViewItem> TaskList { get; private set; }

        DispatcherTimer _idle;
        private const int _idleSeconds = 4;

        public TaskWindow()
        {
            if (Current != null)
                throw new InvalidOperationException("Can only have one TaskWindow at a time");
            Current = this;
            InitializeComponent();
            TaskList = new ObservableCollection<TaskViewItem>();
            //{
            //    new UploadTaskViewItem("test.zip") { Progress = 50, UploadURL = "test" },
            //    new UploadTaskViewItem("hello.png") { Progress = 20 },
            //    new UploadTaskViewItem("mya.exe") { Progress = 100 }
            //};
            //TaskList.PropertyChanged += (s, e) =>
            //{
            //    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("OverallProgress"));
            //};

            InputManager.Current.PreProcessInput += Idle_PreProcessInput;
            _idle = new DispatcherTimer();
            _idle.Interval = TimeSpan.FromSeconds(_idleSeconds);
            _idle.Tick += Idle_Tick;

            this.Loaded += (s, e) =>
            {
                var desktopWorkingArea = System.Windows.SystemParameters.WorkArea;
                this.Left = desktopWorkingArea.Right - this.Width - 20;
                this.Top = desktopWorkingArea.Top;
                this.Height = desktopWorkingArea.Height;
                this.InvalidateMeasure();
                Canvas.SetTop(containerGrid, this.Height - containerGrid.DesiredSize.Height);
                _idle.IsEnabled = true;
            };
        }

        public new async Task Show()
        {
            if (!this.IsLoaded)
            {
                (this as Window).Show();
            }
            else if (!this.IsVisible)
            {
                Canvas.SetTop(containerGrid, this.Height + 1);
                (this as Window).Show();
                await Task.Delay(100);
                await SetHeightTo(containerGrid.DesiredSize.Height);
            }
            else if (this.IsMinimized)
            {
                await SetHeightTo(containerGrid.DesiredSize.Height);
            }
            this.IsMinimized = false;
            AutoMinimizeEnabled = true;
            this.Notifing = false;
        }
        public new async Task Hide()
        {
            if (!this.IsLoaded || !this.IsVisible)
                return;
            await SetHeightTo(-1);
            base.Hide();
            TaskList.Where(t => t.Status == TaskViewItem.TaskStatus.Executed)
                .ToList()
                .ForEach(t => TaskList.Remove(t));
        }
        public new async void Close()
        {
            if (Current == this)
                Current = null;
            if (this.IsLoaded || this.IsVisible)
            {
                await SetHeightTo(-1);
                this.DoRender();
            }
            base.Close();
        }

        public async Task ActivateNext()
        {
            foreach (var t in TaskList)
            {
                if (t.HeroAvailable && t.Status != TaskViewItem.TaskStatus.Executed)
                {
                    await Show();
                    t.HeroCommand.Execute(null);
                    return;
                }
            }
        }
        public void Notify()
        {
            if (!this.IsVisible)
                this.Show();
            else if (IsMinimized)
                Notifing = true;
            else
                AutoMinimizeEnabled = false;
        }
        public void AddTask(TaskViewItem item)
        {
            if (_idle.IsEnabled)
            {
                _idle.IsEnabled = false;
                _idle.IsEnabled = true;
            }
            IsMinimized = false;
            Notifing = false;
            item.PropertyChanged += Item_PropertyChanged;
            this.Show();
            if (TaskList.Count < 1)
            {
                SetHeightTo(80);
            }
            else
            {
                SetHeightTo(containerGrid.DesiredSize.Height + 35);
            }
            TaskList.Add(item);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("OverallProgress"));
        }
        public void RemoveTask(TaskViewItem item)
        {
            if (!TaskList.Contains(item))
                return;
            TaskList.Remove(item);
            if (!IsMinimized)
                this.Show();
            item.PropertyChanged -= Item_PropertyChanged;
        }

        private void Idle_Tick(object sender, EventArgs e)
        {
            if (!this.IsVisible)
                return;
            if (!this.IsMouseOver && !IsMinimized && AutoMinimizeEnabled)
            {
                this.AutoMinimize();
            }
            else if (IsMinimized && Notifing)
            {
                NotifyBounce();
            }
        }
        private void Idle_PreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (!this.IsVisible)
                return;
            if (IsMinimized && this.IsMouseOver)
            {
                this.Show();
            }
            if (_idle.IsEnabled)
            {
                //restart idle timer
                _idle.IsEnabled = false;
                _idle.IsEnabled = true;
            }
        }
        private async void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Progress")
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("OverallProgress"));
            }
            if (e.PropertyName == "Status" && ((TaskViewItem)sender).Status == TaskViewItem.TaskStatus.Executed)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("OverallProgress"));
                await Task.Delay(1000);
                if (TaskList.Any(t => t.Status == TaskViewItem.TaskStatus.Complete ||
                                      t.Status == TaskViewItem.TaskStatus.InProgress))
                {
                    AutoMinimizeEnabled = true;
                }
                else
                {
                    this.Hide();
                }
            }
        }
        private void MinimizeExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            this.Hide();
        }
        private async Task SetHeightTo(double newHeight, bool headerVisible = true, double durationMultiplier = 1)
        {
            var diff = Math.Abs(newHeight - (this.Height - Canvas.GetTop(containerGrid)));
            var duration = (int)(diff * (durationMultiplier * 3));

            var _sb = new Storyboard();

            var ease = new PowerEase() { Power = 5 };
            DoubleAnimation top = new DoubleAnimation(this.Height - newHeight, new Duration(TimeSpan.FromMilliseconds(duration)));
            top.EasingFunction = ease;
            top.SetValue(Storyboard.TargetProperty, containerGrid);
            top.SetValue(Storyboard.TargetPropertyProperty, new PropertyPath(Canvas.TopProperty));

            DoubleAnimation opacity = new DoubleAnimation(headerVisible ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(duration)));
            opacity.EasingFunction = ease;
            opacity.SetValue(Storyboard.TargetProperty, headerGrid);
            opacity.SetValue(Storyboard.TargetPropertyProperty, new PropertyPath(Grid.OpacityProperty));

            _sb.Children.Add(top);
            _sb.Children.Add(opacity);
            await BeginStoryboardAsync(_sb, HandoffBehavior.SnapshotAndReplace, true);
        }
        private void AutoMinimize()
        {
            if (IsMinimized)
                return;
            IsMinimized = true;

            int time = (int)(Math.Max((this.Height - Canvas.GetTop(containerGrid)) - 30, 30) * 10) + 600;
            int secondStep = time - 600;
            int thirdStep = secondStep + 200;
            var _sb = new Storyboard();
            _sb.AutoReverse = false;

            var firstEase = new PowerEase() { Power = 5 };
            var secondEase = new PowerEase() { Power = 2 };

            DoubleAnimationUsingKeyFrames top = new DoubleAnimationUsingKeyFrames();
            top.Duration = new Duration(TimeSpan.FromMilliseconds(time));
            EasingDoubleKeyFrame tf1 = new EasingDoubleKeyFrame(this.Height - 30,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(secondStep)), firstEase);
            EasingDoubleKeyFrame tf2 = new EasingDoubleKeyFrame(this.Height - 30,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(thirdStep)));
            EasingDoubleKeyFrame tf3 = new EasingDoubleKeyFrame(this.Height - 15,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(time)), secondEase);
            top.KeyFrames.Add(tf1);
            top.KeyFrames.Add(tf2);
            top.KeyFrames.Add(tf3);
            top.SetValue(Storyboard.TargetProperty, containerGrid);
            top.SetValue(Storyboard.TargetPropertyProperty, new PropertyPath(Canvas.TopProperty));

            DoubleAnimationUsingKeyFrames opacity = new DoubleAnimationUsingKeyFrames();
            opacity.Duration = new Duration(TimeSpan.FromMilliseconds(time));
            EasingDoubleKeyFrame of1 = new EasingDoubleKeyFrame(1,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(thirdStep)));
            EasingDoubleKeyFrame of2 = new EasingDoubleKeyFrame(0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(time)), secondEase);
            opacity.KeyFrames.Add(of1);
            opacity.KeyFrames.Add(of2);
            opacity.SetValue(Storyboard.TargetProperty, headerGrid);
            opacity.SetValue(Storyboard.TargetPropertyProperty, new PropertyPath(Grid.OpacityProperty));


            _sb.Children.Add(top);
            _sb.Children.Add(opacity);
            BeginStoryboard(_sb, HandoffBehavior.SnapshotAndReplace, true);
        }
        private async void NotifyBounce()
        {
            if (!IsMinimized)
                return;

            await SetHeightTo(15, true, 8);
            var _sb = new Storyboard();
            _sb.AutoReverse = false;

            var ease = new BounceEase() { Bounces = 2, Bounciness = 2 };
            DoubleAnimation top = new DoubleAnimation(this.Height - 30, new Duration(TimeSpan.FromMilliseconds(1000)));
            top.EasingFunction = ease;
            top.SetValue(Storyboard.TargetProperty, containerGrid);
            top.SetValue(Storyboard.TargetPropertyProperty, new PropertyPath(Canvas.TopProperty));

            _sb.Children.Add(top);
            BeginStoryboard(_sb, HandoffBehavior.SnapshotAndReplace, true);
        }
        private Task BeginStoryboardAsync(Storyboard storyboard, HandoffBehavior handoff = HandoffBehavior.SnapshotAndReplace, bool controllable = true)
        {
            System.Threading.Tasks.TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            if (storyboard == null)
                tcs.SetException(new ArgumentNullException());
            else
            {
                EventHandler onComplete = null;
                onComplete = (s, e) =>
                {
                    Clock clock = s as Clock;
                    if (clock.CurrentState != ClockState.Active)
                    {
                        storyboard.CurrentStateInvalidated -= onComplete;
                        tcs.SetResult(true);
                    }
                };
                storyboard.CurrentStateInvalidated += onComplete;
                storyboard.Begin();
            }
            return tcs.Task;
        }
    }

    [ImplementPropertyChanged]
    public class TaskViewItem : INotifyPropertyChanged
    {
        public enum TaskStatus
        {
            Complete,
            Waiting,
            InProgress,
            Error,
            Executed,
            Canceled
        }
        public TaskStatus Status { get; set; }
        public SolidColorBrush ProgressBrush { get; private set; }
        public Color ProgressColor { get; private set; }
        public string PrimaryText { get; set; }
        public string SecondaryText { get; set; }
        public string ProgressCurrentText { get; set; }
        public string ProgressTargetText { get; set; }
        public double Progress { get; set; }
        public virtual double OverallWeight { get { return 1; } }
        public virtual bool HeroAvailable { get { return false; } }
        public RelayUICommand HeroCommand { get; protected set; }
        public RelayUICommand OpenCommand { get; protected set; }
        public RelayUICommand CopyCommand { get; protected set; }
        public RelayUICommand EmailCommand { get; protected set; }
        public RelayUICommand EditCommand { get; protected set; }
        public RelayUICommand CancelCommand { get; protected set; }

        public TaskViewItem()
        {
            Status = TaskStatus.InProgress;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler Copied;
        protected void OnCopied()
        {
            Copied?.Invoke(this, new EventArgs());
        }
        private void OnStatusChanged()
        {
            switch (Status)
            {
                case TaskStatus.Complete:
                    ProgressBrush = Brushes.PaleGreen;
                    ProgressColor = Colors.PaleGreen;
                    break;
                case TaskStatus.Error:
                    ProgressBrush = Brushes.PaleVioletRed;
                    ProgressColor = Colors.PaleVioletRed;
                    break;
                case TaskStatus.Canceled:
                    ProgressBrush = Brushes.IndianRed;
                    ProgressColor = Colors.IndianRed;
                    break;
                case TaskStatus.InProgress:
                    ProgressBrush = Brushes.PaleGoldenrod;
                    ProgressColor = Colors.PaleGoldenrod;
                    break;
                case TaskStatus.Waiting:
                    ProgressBrush = Brushes.PaleGoldenrod;
                    ProgressColor = Colors.PaleGoldenrod;
                    break;
                case TaskStatus.Executed:
                    ProgressBrush = Brushes.LightGray;
                    ProgressColor = Colors.LightGray;
                    break;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeroAvailable)));
        }
        private void OnProgressChanged()
        {
            if (Progress > 99.9)
            {
                Status = TaskStatus.Complete;
            }
        }
    }
    [ImplementPropertyChanged]
    public class UploadTaskViewItem : TaskViewItem
    {
        public long FileSize { get; set; }
        public string UploadURL { get; set; }
        private CancellationTokenSource _canceler;

        public override bool HeroAvailable
        {
            get
            {
                return !String.IsNullOrEmpty(UploadURL)
                    && (Status == TaskStatus.Complete || Status == TaskStatus.InProgress || Status == TaskStatus.Executed);
            }
        }

        private static DataTemplate _copyTemplate;

        public UploadTaskViewItem(string primary, string secondary, CancellationTokenSource canceler)
        {
            PrimaryText = primary;
            SecondaryText = secondary;
            _canceler = canceler;
            Progress = 5;

            const string copyTemplate =
@"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
    <Grid>
        <Viewbox Margin=""5"">
            <Path Fill=""Black"" Opacity=""0.8"" Width=""26"" Height=""26""  Data=""{StaticResource PathCopy26}"" />
        </Viewbox>
    </Grid>
</DataTemplate>";
            if (_copyTemplate == null)
                _copyTemplate = (DataTemplate)XamlReader.Parse(copyTemplate);

            HeroCommand = new RelayUICommand(
                _ => CopyExecuted(_),
                _ => HeroAvailable,
                "Copy to clipboard",
                _copyTemplate);

            OpenCommand = new RelayUICommand(
                _ => OpenExecuted(_),
                _ => !String.IsNullOrEmpty(UploadURL) && (Status == TaskStatus.Complete || Status == TaskStatus.Executed || Status == TaskStatus.InProgress));
            CopyCommand = new RelayUICommand(
                _ => CopyExecuted(_),
                _ => HeroAvailable);
            EmailCommand = new RelayUICommand(
                _ => EmailExecuted(_),
                _ => !String.IsNullOrEmpty(UploadURL) && (Status == TaskStatus.Complete || Status == TaskStatus.Executed || Status == TaskStatus.InProgress));
            EditCommand = new RelayUICommand(
                _ => EditExecuted(_),
                _ => Status == TaskStatus.Complete || Status == TaskStatus.InProgress || Status == TaskStatus.Executed);
            CancelCommand = new RelayUICommand(
                _ => CancelExecuted(_),
                _ => Status == TaskStatus.Waiting || Status == TaskStatus.InProgress);
        }

        private void OpenExecuted(object param)
        {
            System.Diagnostics.Process.Start(UploadURL);
            SetExecuted();
        }
        private void EmailExecuted(object param)
        {
            System.Diagnostics.Process.Start("mailto:?subject=Clowd Attachment&body=" + UploadURL);
            SetExecuted();
        }
        private void EditExecuted(object param)
        {
            //throw new NotImplementedException();
        }
        private void CopyExecuted(object param)
        {
            Clipboard.SetText(UploadURL);
            OnCopied();
            SetExecuted();
        }
        private void CancelExecuted(object param)
        {
            _canceler.Cancel();
            SecondaryText = "Canceled.";
            Status = TaskStatus.Canceled;
        }

        public void SetExecuted()
        {
            if (Status != TaskStatus.InProgress && Status != TaskStatus.Waiting)
            {
                Status = TaskStatus.Executed;
            }
        }
    }
}
