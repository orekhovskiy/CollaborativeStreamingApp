using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CollaborativeStreamingApp
{
    public readonly struct PixelBitsDistribution
    {
        public PixelBitsDistribution(int yBits, int uBits, int vBits)
        {
            YBits = yBits;
            UBits = uBits;
            VBits = vBits;
        }
        public readonly int YBits;
        public readonly int UBits;
        public readonly int VBits;
    };
}
