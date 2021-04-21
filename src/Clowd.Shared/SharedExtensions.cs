using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public static class SharedExtensions
    {
        public static async Task WithTimeout(Task task, int timeoutMs)
        {
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) == task)
            {
                return;
            }
            else
            {
                throw new TimeoutException("The operation has timed out");
            }
        }

        public static async Task<T> WithTimeout<T>(this Task<T> task, int timeoutMs)
        {
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) == task)
            {
                return await task;
            }
            else
            {
                throw new TimeoutException("The operation has timed out");
            }
        }
    }
}
