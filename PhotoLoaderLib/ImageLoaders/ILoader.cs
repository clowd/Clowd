using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace PhotoLoader.ImageLoaders
{
    internal interface ILoader
    {
        Stream Load(string source);
    }
}
