// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.OutputSettings
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

namespace NReco.VideoConverter
{
  public class OutputSettings
  {
    /// <summary>
    /// Explicit sample rate for audio stream. Usual rates are: 44100, 22050, 11025
    /// </summary>
    public int? AudioSampleRate = new int?();
    /// <summary>
    /// Explicit video rate for video stream. Usual rates are: 30, 25
    /// </summary>
    public int? VideoFrameRate = new int?();
    /// <summary>Number of video frames to record</summary>
    public int? VideoFrameCount = new int?();
    /// <summary>Get or set max duration (in seconds)</summary>
    public float? MaxDuration = new float?();
    /// <summary>
    /// Audio codec (complete list of audio codecs: ffmpeg -codecs)
    /// </summary>
    public string AudioCodec;
    /// <summary>
    /// Video frame size (common video sizes are listed in VideoSizes
    /// </summary>
    public string VideoFrameSize;
    /// <summary>
    /// Video codec (complete list of video codecs: ffmpeg -codecs)
    /// </summary>
    public string VideoCodec;
    /// <summary>Extra custom FFMpeg parameters for 'output'</summary>
    /// <remarks>
    /// FFMpeg command line arguments inserted after input file parameter (-i) but before output file
    /// </remarks>
    public string CustomOutputArgs;

    public void SetVideoFrameSize(int width, int height)
    {
      this.VideoFrameSize = string.Format("{0}x{1}", (object) width, (object) height);
    }

    internal void CopyTo(OutputSettings outputSettings)
    {
      outputSettings.AudioSampleRate = this.AudioSampleRate;
      outputSettings.AudioCodec = this.AudioCodec;
      outputSettings.VideoFrameRate = this.VideoFrameRate;
      outputSettings.VideoFrameCount = this.VideoFrameCount;
      outputSettings.VideoFrameSize = this.VideoFrameSize;
      outputSettings.VideoCodec = this.VideoCodec;
      outputSettings.MaxDuration = this.MaxDuration;
      outputSettings.CustomOutputArgs = this.CustomOutputArgs;
    }
  }
}
