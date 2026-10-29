using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Every binding here is blittable by construction (README R18): structs go by pointer, bools are
// bytes, strings are (pointer, length). Turning runtime marshalling off makes any slip a load-time
// error instead of a silent marshalling stub.
[assembly: DisableRuntimeMarshalling]

// The resolver in InteropLibrary loads an absolute path; this constrains the fallback so a planted
// prisc_interop.dll in the working directory or on PATH is never a load path.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
