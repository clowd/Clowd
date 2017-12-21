using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileUploadLib
{
    internal static class UploadUtil
    {
        public static string SubstituteUploadUrl(string template, string mime, string uploadKey)
        {
            return template
                .Replace("#{uk}", uploadKey)
                .Replace("#{mt}", mime);
        }
    }
}
