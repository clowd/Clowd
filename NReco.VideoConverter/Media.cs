// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.Media
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

using System.IO;

namespace NReco.VideoConverter
{
  internal class Media
  {
    public string Filename { get; set; }

    public string Format { get; set; }

    public Stream DataStream { get; set; }
  }
}
