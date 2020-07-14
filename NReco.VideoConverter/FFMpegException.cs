// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.FFMpegException
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

using System;

namespace NReco.VideoConverter
{
  /// <summary>
  /// The exception that is thrown when FFMpeg process retruns non-zero error exit code
  /// </summary>
  public class FFMpegException : Exception
  {
    /// <summary>Get FFMpeg process error code</summary>
    public int ErrorCode { get; private set; }

    public FFMpegException(int errCode, string message)
      : base(string.Format("{0} (exit code: {1})", (object) message, (object) errCode))
    {
      this.ErrorCode = errCode;
    }
  }
}
