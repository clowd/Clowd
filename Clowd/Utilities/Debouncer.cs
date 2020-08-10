using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Utilities
{
    class Debouncer : IDisposable
    {
        private CancellationTokenSource lastCToken;
        private int milliseconds;

        public Debouncer(int milliseconds = 300)
        {
            this.milliseconds = milliseconds;
        }

        public void Debounce(Action action)
        {
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
            Cancel(lastCToken);
        }

        ~Debouncer()
        {
            Dispose();
        }
    }
}
