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

        [Script("return md5(array);")]
        public static string Md5(this ArrayBuffer array)
        {
            return null;
        }
        [Script("return md5(array);")]
        public static string Md5(this Array array)
        {
            return null;
        }
        [Script("return md5(str);")]
        public static string Md5(this string str)
        {
            return null;
        }
    }
}
