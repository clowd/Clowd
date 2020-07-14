// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.FFMpegUserCredential
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

using System.Security;

namespace NReco.VideoConverter
{
  /// <summary>
  /// Represents user credential used when starting FFMpeg process.
  /// </summary>
  public sealed class FFMpegUserCredential
  {
    /// <summary>
    /// Gets the user name to be used when starting FFMpeg process.
    /// </summary>
    public string UserName { get; private set; }

    /// <summary>
    /// Gets a secure string that contains the user password to use when starting FFMpeg process.
    /// </summary>
    public SecureString Password { get; private set; }

    /// <summary>
    /// Gets a value that identifies the domain to use when starting FFMpeg process.
    /// </summary>
    public string Domain { get; private set; }

    public FFMpegUserCredential(string userName, SecureString password)
    {
      this.UserName = userName;
      this.Password = password;
    }

    public FFMpegUserCredential(string userName, SecureString password, string domain)
      : this(userName, password)
    {
      this.Domain = domain;
    }
  }
}
