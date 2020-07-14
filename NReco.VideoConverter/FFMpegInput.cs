// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.FFMpegInput
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

namespace NReco.VideoConverter
{
  /// <summary>
  /// The exception that is thrown when FFMpeg process retruns non-zero error exit code
  /// </summary>
  public class FFMpegInput
  {
    /// <summary>FFMpeg input (filename, URL or demuxer parameter)</summary>
    public string Input { get; set; }

    /// <summary>
    /// Input media stream format (if null ffmpeg tries to automatically detect format).
    /// </summary>
    public string Format { get; set; }

    /// <summary>Extra custom FFMpeg parameters for this input.</summary>
    /// <remarks>
    /// These FFMpeg command line arguments inserted before input specifier (-i).
    /// </remarks>
    public string CustomInputArgs { get; set; }

    public FFMpegInput(string input)
      : this(input, (string) null)
    {
    }

    public FFMpegInput(string input, string format)
    {
      this.Input = input;
      this.Format = format;
    }
  }
}
