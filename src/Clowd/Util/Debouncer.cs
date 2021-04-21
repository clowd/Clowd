using System;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Util
{
    class Debouncer : IDisposable
    {
        private CancellationTokenSource lastCToken;
        private int milliseconds;
        private bool disposed;

        public Debouncer(int milliseconds = 300)
        {
            this.milliseconds = milliseconds;
        }

        public void Debounce(Action action)
        {
            if (disposed)
                return;

            Cancel(lastCToken);

            var tokenSrc = lastCToken = new CancellationTokenSource();

            Task.Delay(milliseconds).ContinueWith(task =>
            {
                action();
            }, tokenSrc.Token);
        }

        public void Cancel(CancellationTokenSource source)
        {
            if (source != null)
            {
                source.Cancel();
                source.Dispose();
            }
        }

        public void Dispose()
        {
            disposed = true;
            Cancel(lastCToken);
        }

        ~Debouncer()
        {
            Dispose();
        }
    }
}
