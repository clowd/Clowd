using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
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
            var timer = new Timer();
            if (synchronized)
                timer.SynchronizingObject = new SynchronizationContextProxy();
            timer.AutoReset = true;
            timer.Interval = interval.TotalMilliseconds;
            timer.Elapsed += (sender, args) => action();
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
