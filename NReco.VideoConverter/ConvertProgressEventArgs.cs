// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.ConvertProgressEventArgs
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

using System;

namespace NReco.VideoConverter
{
  /// <summary>Provides data for ConvertProgress event</summary>
  public class ConvertProgressEventArgs : EventArgs
  {
    /// <summary>Total media stream duration</summary>
    public TimeSpan TotalDuration { get; private set; }

    /// <summary>Processed media stream duration</summary>
    public TimeSpan Processed { get; private set; }

    public ConvertProgressEventArgs(TimeSpan processed, TimeSpan totalDuration)
    {
      this.TotalDuration = totalDuration;
      this.Processed = processed;
    }
  }
}
