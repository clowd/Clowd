using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop.Com
{
	[ComImport]
	[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IVirtualDesktopManager
	{
		bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);

		Guid GetWindowDesktopId(IntPtr topLevelWindow);

		void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
	}

	public class VirtualDesktopManager
	{
		public static IVirtualDesktopManager CreateNew()
		{
			var clsid = new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a");
			return (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid));
		}
	}

	// see also: 
	//  "C:\Program Files (x86)\Windows Kits\10\Include\10.0.10240.0\um\ShObjIdl.h"
	//  https://msdn.microsoft.com/en-us/library/windows/desktop/mt186440%28v=vs.85%29.aspx
	//  http://www.cyberforum.ru/blogs/105416/blog3671.html
}
