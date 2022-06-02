using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Clowd.PlatformUtil.Windows
{
    [Flags]
    public enum WinFileAttributes
    {
        None = 0,

        /// <summary>The file contains debugging information or is compiled with debugging features enabled.</summary>
        Debug = 0x00000001,

        /// <summary>The file's version structure was created dynamically; therefore, some of the members in this structure may be empty or incorrect.</summary>
        InfoInferred = 0x00000010,

        /// <summary>The file has been modified and is not identical to the original shipping file of the same version number.</summary>
        Patched = 0x00000004,

        /// <summary>The file is a development version, not a commercially released product.</summary>
        PreRelease = 0x00000002,

        /// <summary>The file was not built using standard release procedures.</summary>
        PrivateBuild = 0x00000008,

        /// <summary>The file was built by the original company using standard release procedures but is a variation of the normal file of the same version number.</summary>
        SpecialBuild = 0x00000020,
    }

    [Flags]
    public enum WinImageOS
    {
        Unknown = 0,
        DOS = 0x00010000,
        NT = 0x00040000,
        Windows_16 = 0x00000001,
        Windows_32 = 0x00000004,
        OS2_16 = 0x00020000,
        OS2_32 = 0x00030000,
        PM_16 = 0x00000002,
        PM_32 = 0x00000003,
    }

    public enum WinFileType
    {
        Unknown = 0,
        ExeApp = 1,
        DynamicLib = 2,
        Driver = 3,
        Font = 4,
        VirtualDevice = 5,
        StaticLib = 7,
    }

    public enum WinImageMachineType
    {
        Unknown = 0,
        x86 = 1,
        x64 = 2,
        IA64 = 3,
    }

    public unsafe class PeVersionInfo
    {
        [DllImport("version.dll", CharSet = CharSet.Auto, BestFitMapping = false, SetLastError = true)]
        private static extern int GetFileVersionInfoSize(string lptstrFilename, nint dwHandle);

        [DllImport("version.dll", CharSet = CharSet.Auto, BestFitMapping = false, SetLastError = true)]
        private static extern bool GetFileVersionInfo(string lptstrFilename, nint dwHandle, int dwLen, void* buffer);

        [DllImport("version.dll", CharSet = CharSet.Auto, BestFitMapping = false)]
        private static extern bool VerQueryValue(void* pBlock, string lpSubBlock, void** lplpBuffer, out int len);

#pragma warning disable CS0649
        private struct VS_FIXEDFILEINFO
        {
            public uint dwSignature;
            public uint dwStructVersion;
            public uint dwFileVersionMS;
            public uint dwFileVersionLS;
            public uint dwProductVersionMS;
            public uint dwProductVersionLS;
            public uint dwFileFlagsMask;
            public WinFileAttributes dwFileFlags;
            public WinImageOS dwFileOS;
            public WinFileType dwFileType;
            public uint dwFileSubtype;
            public uint dwFileDateMS;
            public uint dwFileDateLS;
        }

        private struct IMAGE_DOS_HEADER
        {
            public ushort e_magic; // must be 0x5A4D
            public ushort e_lfanew; // offset for start of IMAGE_NT_HEADERS
        }

        private struct IMAGE_NT_HEADERS
        {
            public uint signature; // must be 0x00004550

            //IMAGE_FILE_HEADER fileHeader;
            public ushort machine;
        }

#pragma warning restore CS0649

        private static string ConvertTo8DigitHex(int value)
        {
            string s = Convert.ToString(value, 16);
            s = s.ToUpper(CultureInfo.InvariantCulture);
            if (s.Length == 8)
            {
                return s;
            }
            else
            {
                StringBuilder b = new StringBuilder(8);
                for (int l = s.Length; l < 8; l++)
                    b.Append("0");
                b.Append(s);
                return b.ToString();
            }
        }

        private static bool GetRootBlock(void* memPtr, VS_FIXEDFILEINFO** fileInfo)
        {
            var path = @"\";
            return VerQueryValue(memPtr, path, (void**)fileInfo, out var memLen);
        }

        private static int GetVarEntry(void* memPtr)
        {
            var path = @"\VarFileInfo\Translation";
            void* memRef;
            if (VerQueryValue(memPtr, path, &memRef, out _))
                return (Marshal.ReadInt16((IntPtr)memRef) << 16) + Marshal.ReadInt16((IntPtr)((long)memRef + 2));
            return 0x040904E4;
        }

        private static string GetStringEntry(void* memPtr, string codepage, string name)
        {
            var path = $@"\StringFileInfo\{codepage}\{name}";
            void* memRef;
            if (VerQueryValue(memPtr, path, &memRef, out var _) && memRef != (void*)0)
                return Marshal.PtrToStringAuto((IntPtr)memRef);
            return "";
        }

        private static WinImageMachineType GetImageType(string filename)
        {
            try
            {
                // https://blog.kowalczyk.info/articles/pefileformat.html
                using var fs = File.OpenRead(filename);
                using var br = new BinaryReader(fs);

                // check DOS signature
                var e_magic = br.ReadUInt16();
                if (e_magic != 0x5A4D) // MZ
                    return WinImageMachineType.Unknown;

                // skip to e_lfanew in the _IMAGE_DOS_HEADER 
                // there are (14 + 4 + 2 + 10) ushorts before it in the structure
                fs.Position = 60;

                // seek to e_lfanew (which is the file offset of PE header)
                fs.Position = br.ReadUInt16();

                // check PE signature
                var peSig = br.ReadUInt32();
                if (peSig != 0x00004550) // PE00
                    return WinImageMachineType.Unknown;

                // IMAGE_FILE_HEADER/machine
                switch (br.ReadUInt16())
                {
                    case 0x014c: return WinImageMachineType.x86;
                    case 0x0200: return WinImageMachineType.IA64;
                    case 0x8664: return WinImageMachineType.x64;
                    default: return WinImageMachineType.Unknown;
                }
            }
            catch
            {
                return WinImageMachineType.Unknown;
            }
        }

        public string FileName { get; init; }
        public string FileDescription { get; init; }
        public string FileVersion { get; init; }
        public DateTime FileDate { get; init; }
        public WinFileType FileType { get; init; }
        public WinFileAttributes FileAttributes { get; init; }
        public WinImageOS ImageOS { get; init; }
        public WinImageMachineType ImageMachineType { get; init; }

        public string Comments { get; init; }
        public string CompanyName { get; init; }
        public string InternalName { get; init; }
        public string LegalCopyright { get; init; }
        public string LegalTrademarks { get; init; }
        public string OriginalFilename { get; init; }
        public string ProductName { get; init; }
        public string ProductVersion { get; init; }
        public string PrivateBuild { get; init; }
        public string SpecialBuild { get; init; }

        public static PeVersionInfo ReadFromFile(string filename)
        {
            filename = Path.GetFullPath(filename);
            var size = GetFileVersionInfoSize(filename, 0);
            if (size <= 0)
                return null; // probably does not have a resource section

            byte* buf = stackalloc byte[size];
            if (!GetFileVersionInfo(filename, 0, size, buf))
                return null;

            var codepage = ConvertTo8DigitHex(GetVarEntry(buf));

            DateTime date = default;
            WinFileType type = WinFileType.Unknown;
            WinImageOS os = WinImageOS.Unknown;
            WinFileAttributes attr = WinFileAttributes.None;

            VS_FIXEDFILEINFO* rootBlock;
            if (GetRootBlock(buf, &rootBlock) && (IntPtr)rootBlock != IntPtr.Zero)
            {
                date = DateTime.FromFileTimeUtc(((long)rootBlock->dwFileDateMS) << 32 & rootBlock->dwFileDateLS);
                type = rootBlock->dwFileType;
                attr = rootBlock->dwFileFlags;
                os = rootBlock->dwFileOS;
            }

            return new PeVersionInfo
            {
                FileName = Path.GetFileName(filename),
                FileDescription = GetStringEntry(buf, codepage, "FileDescription"),
                FileVersion = GetStringEntry(buf, codepage, "FileVersion"),
                FileDate = date,
                FileType = type,
                FileAttributes = attr,
                ImageOS = os,
                ImageMachineType = GetImageType(filename),
                Comments = GetStringEntry(buf, codepage, "Comments"),
                CompanyName = GetStringEntry(buf, codepage, "CompanyName"),
                InternalName = GetStringEntry(buf, codepage, "InternalName"),
                LegalCopyright = GetStringEntry(buf, codepage, "LegalCopyright"),
                LegalTrademarks = GetStringEntry(buf, codepage, "LegalTrademarks"),
                OriginalFilename = GetStringEntry(buf, codepage, "OriginalFilename"),
                ProductName = GetStringEntry(buf, codepage, "ProductName"),
                ProductVersion = GetStringEntry(buf, codepage, "ProductVersion"),
                PrivateBuild = GetStringEntry(buf, codepage, "PrivateBuild"),
                SpecialBuild = GetStringEntry(buf, codepage, "SpecialBuild"),
            };
        }
    }
}
