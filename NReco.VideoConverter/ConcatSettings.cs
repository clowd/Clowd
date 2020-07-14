// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.ConcatSettings
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

namespace NReco.VideoConverter
{
  /// <summary>Media concatenation setting</summary>
  /// <inherit>NReco.VideoConverter.OutputSettings</inherit>
  public class ConcatSettings : OutputSettings
  {
    /// <summary>Determine whether audio stream</summary>
    public bool ConcatVideoStream = true;
    /// <summary>Seek to position (in seconds) before converting</summary>
    public bool ConcatAudioStream = true;
  }
}
