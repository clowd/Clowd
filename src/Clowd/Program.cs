using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    internal class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // fast exit for "watch" command
            if (args.Length > 0 && args[0].Equals("watch", StringComparison.InvariantCulture))
            {
                ProcessWatcher.Run(args.Skip(1).ToArray());
                return;
            }

            var application = new App();
            application.InitializeComponent();
            application.Run();
        }
    }
}
