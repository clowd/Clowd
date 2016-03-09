using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bridge;
using Bridge.Html5;

namespace ScriptLib
{
    public static class Extensions
    {
        public static void SaveAs(this Blob data, string filename)
        {
            Script.Call("saveAs", data, filename);
        }
    }
}
