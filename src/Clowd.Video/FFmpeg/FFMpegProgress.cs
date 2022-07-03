using System;
using System.Text.RegularExpressions;

namespace Clowd.Video.FFmpeg
{
    internal class FFMpegProgress
    {
        public float? Seek { get; set; }
        
        public float? MaxDuration { get; set; }
        
        private static Regex DurationRegex = new Regex("Duration:\\s(?<duration>[0-9:.]+)([,]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static Regex ProgressRegex = new Regex("time=(?<progress>[0-9:.]+)\\s", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private Action<ConvertProgressEventArgs> ProgressCallback;
        private ConvertProgressEventArgs lastProgressArgs;
        private bool Enabled = true;
        private int progressEventCount;

        internal FFMpegProgress(Action<ConvertProgressEventArgs> progressCallback, bool enabled)
        {
            this.ProgressCallback = progressCallback;
            this.Enabled = enabled;
        }

        internal void Reset()
        {
            this.progressEventCount = 0;
            this.lastProgressArgs = (ConvertProgressEventArgs)null;
        }

        internal void ParseLine(string line)
        {
            if (!this.Enabled)
                return;
            TimeSpan totalDuration1 = this.lastProgressArgs != null ? this.lastProgressArgs.TotalDuration : TimeSpan.Zero;
            Match match1 = FFMpegProgress.DurationRegex.Match(line);
            if (match1.Success)
            {
                TimeSpan result = TimeSpan.Zero;
                if (TimeSpan.TryParse(match1.Groups["duration"].Value, out result))
                {
                    TimeSpan totalDuration2 = totalDuration1.Add(result);
                    this.lastProgressArgs = new ConvertProgressEventArgs(TimeSpan.Zero, totalDuration2);
                }
            }

            Match match2 = FFMpegProgress.ProgressRegex.Match(line);
            if (!match2.Success)
                return;
            TimeSpan result1 = TimeSpan.Zero;
            if (!TimeSpan.TryParse(match2.Groups["progress"].Value, out result1))
                return;
            if (this.progressEventCount == 0)
                totalDuration1 = this.CorrectDuration(totalDuration1);
            this.lastProgressArgs = new ConvertProgressEventArgs(result1, totalDuration1 != TimeSpan.Zero ? totalDuration1 : result1);
            this.ProgressCallback(this.lastProgressArgs);
            ++this.progressEventCount;
        }

        private TimeSpan CorrectDuration(TimeSpan totalDuration)
        {
            if (totalDuration != TimeSpan.Zero)
            {
                if (this.Seek.HasValue)
                {
                    TimeSpan ts = TimeSpan.FromSeconds((double)this.Seek.Value);
                    totalDuration = totalDuration > ts ? totalDuration.Subtract(ts) : TimeSpan.Zero;
                }

                if (this.MaxDuration.HasValue)
                {
                    TimeSpan timeSpan = TimeSpan.FromSeconds((double)this.MaxDuration.Value);
                    if (totalDuration > timeSpan)
                        totalDuration = timeSpan;
                }
            }

            return totalDuration;
        }

        internal void Complete()
        {
            if (!this.Enabled || this.lastProgressArgs == null || !(this.lastProgressArgs.Processed < this.lastProgressArgs.TotalDuration))
                return;
            this.ProgressCallback(new ConvertProgressEventArgs(this.lastProgressArgs.TotalDuration, this.lastProgressArgs.TotalDuration));
        }
    }
}
