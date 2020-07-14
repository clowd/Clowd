// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.ConvertSettings
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

namespace NReco.VideoConverter
{
  /// <summary>Media conversion setting</summary>
  /// <inherit>NReco.VideoConverter.OutputSettings</inherit>
  public class ConvertSettings : OutputSettings
  {
    /// <summary>Seek to position (in seconds) before converting</summary>
    public float? Seek = new float?();
    /// <summary>Add silent audio stream to output</summary>
    public bool AppendSilentAudioStream;
    /// <summary>Extra custom FFMpeg parameters for 'input'</summary>
    /// <remarks>
    /// FFMpeg command line arguments inserted before input file parameter (-i)
    /// </remarks>
    public string CustomInputArgs;
  }
}
