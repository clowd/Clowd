using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screeney.Audio
{
    internal class SilenceProvider : IWaveProvider
    {
        public SilenceProvider(WaveFormat wf) { this.WaveFormat = wf; }

        public int Read(byte[] buffer, int offset, int count)
        {
            buffer.Initialize();
            return count;
        }

        public WaveFormat WaveFormat { get; private set; }
    }
}
