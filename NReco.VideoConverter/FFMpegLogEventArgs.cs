// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.FFMpegLogEventArgs
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

using System;

namespace NReco.VideoConverter
{
  /// <summary>Provides data for log received event</summary>
  public class FFMpegLogEventArgs : EventArgs
  {
    /// <summary>Log line</summary>
    public string Data { get; private set; }

    public FFMpegLogEventArgs(string logData)
    {
      this.Data = logData;
    }
  }
}
