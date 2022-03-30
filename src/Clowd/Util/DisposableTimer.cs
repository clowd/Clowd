using System;
using System.Windows.Threading;
using Timer = System.Timers.Timer;

namespace Clowd.Util
{
    public static class DisposableTimer
    {
        public static IDisposable Start(TimeSpan interval, Action action)
        {
            return Start(interval, action, true);
        }
        public static IDisposable Start(TimeSpan interval, Action action, bool synchronized)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;

            var timer = new Timer();
            timer.AutoReset = true;
            timer.Interval = interval.TotalMilliseconds;
            timer.Elapsed += (sender, args) =>
            {
                if (synchronized && dispatcher != null)
                {
                    dispatcher.Invoke(action);
                }
                else
                {
                    action();
                }
            };
            timer.Start();

            return new timerDisposer(timer);
        }

        private class timerDisposer : IDisposable
        {
            private Timer _timer;

            public timerDisposer(Timer timer)
            {
                _timer = timer;
            }

            public void Dispose()
            {
                _timer?.Stop();
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
