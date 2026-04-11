using System;

namespace Clowd.Ui.Models.Upload;

[Flags]
public enum SupportedUploadType
{
    None = 0,
    Image = 1 << 0,
    Video = 1 << 1,
    Text  = 1 << 2,
    Binary = 1 << 3,
    All = Image | Video | Text | Binary,
}
