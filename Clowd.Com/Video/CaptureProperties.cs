using Sonic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Clowd.Com.Video
{
    class CaptureProperties : ICloneable
    {
        public int X { get; set; }
        public int Y { get; set; }
        public short BitCount { get; set; } = 32;
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public int Size => COMHelper.ALIGN16(PixelWidth) * COMHelper.ALIGN16(Math.Abs(PixelHeight)) * BitCount / 8;

        object ICloneable.Clone()
        {
            return MemberwiseClone();
        }

        public CaptureProperties Clone()
        {
            return (CaptureProperties)MemberwiseClone();
        }
    }
}
