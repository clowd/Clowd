using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop
{
    public struct SIZE
    {
        public int cx;

        public int cy;

        public SIZE(int x, int y)
        {
            this.cx = x;
            this.cy = y;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is SIZE))
            {
                return false;
            }
            SIZE sIZE = (SIZE)obj;
            if (sIZE.cx != this.cx)
            {
                return false;
            }
            return sIZE.cy == this.cy;
        }

        public override int GetHashCode()
        {
            return this.cx ^ this.cy;
        }

        public static bool operator ==(SIZE Size1, SIZE Size2)
        {
            return Size1.Equals(Size2);
        }

        public static bool operator !=(SIZE Size1, SIZE Size2)
        {
            return !Size1.Equals(Size2);
        }

        public override string ToString()
        {
            return string.Format("{{cx={0}, cy={1}}}", this.cx, this.cy);
        }
    }
}
