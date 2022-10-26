using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Clowd.Drawing
{
    internal partial class CursorResources : EmbeddedResource
    {
        public static Cursor GetCursor(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Cursors", fileName);
            if (File.Exists(path))
            {
                return new Cursor(path, true);
            }

            return new Cursor(GetStream(RSX_NS, fileName), true);
        }

        private const string RSX_NS = "Clowd.Drawing.Cursors";

        public CursorResources() : base(Assembly.GetExecutingAssembly(), RSX_NS) { }
    }
}
