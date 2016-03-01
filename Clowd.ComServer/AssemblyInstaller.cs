using Clowd.ComServer.InteropServices;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace Clowd.ComServer
{
    [ComVisible(false)]
    public static class AssemblyInstaller
    {
        public static bool Install()
        {
            if (!IsUserAdministrator())
                return false;

            try { PerformAction(false); } catch { }

            return PerformAction(true);
        }
        public static bool Uninstall()
        {
            if (!IsUserAdministrator())
                return false;

            return PerformAction(false);
        }
        public static List<Process> WhoIsLocking()
        {
            return FileUtil.WhoIsLocking(Path.GetFullPath(Assembly.GetExecutingAssembly().Location));
        }
        public static bool CheckIsInstalled()
        {
            var cat = new Sonic.DSCategory(new Guid(DirectShow.BaseClasses.AMovieSetup.CLSID_VideoInputDeviceCategory));
            foreach (var inputDevice in cat)
            {
                if (inputDevice.Filter != null)
                {
                    if (inputDevice.Filter.Name.Equals("clowd-virtual-camera", StringComparison.InvariantCultureIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        public static bool IsUserAdministrator()
        {
            //bool value to hold our return value
            bool isAdmin;
            try
            {
                //get the currently logged in user
                WindowsIdentity user = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(user);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (UnauthorizedAccessException)
            {
                isAdmin = false;
            }
            catch (Exception)
            {
                isAdmin = false;
            }
            return isAdmin;
        }

        private const String strImplementedCategoriesSubKey = "Implemented Categories";
        private const String strContextMenuShellName = "Upload with Clowd";
        private const String strManagedCategoryGuid = "{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}";

        private static bool PerformAction(bool install)
        {
            bool success = true;
            //Install DirectShow Filter to 32bit registry
            using (var clsid32 = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32))
            {
                if (!CheckIsInstalled() || !install)
                {
                    // actually accessing Wow6432Node 
                    if (install)
                        success = success && InstallComType(typeof(DShowVideoFilter.ScreenCaptureFilter), clsid32);
                    else
                        success = success && UninstallComType(typeof(DShowVideoFilter.ScreenCaptureFilter), clsid32);
                }
            }
            //using (var view32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
            //using (var clsid32 = view32.OpenSubKey(@"Software\Classes\", true))
            //{
            //    using (var catchAll = clsid32.CreateSubKey(@"*\shell"))
            //    using (var directoryAll = clsid32.CreateSubKey(@"Directory\shell"))
            //        if (install)
            //        {
            //            success = success && InstallContextMenu(catchAll);
            //            success = success && InstallContextMenu(directoryAll);
            //        }
            //        else
            //        {
            //            UninstallContextMenu(catchAll);
            //            UninstallContextMenu(directoryAll);
            //        }
            //}
            return success;
        }
        //private static bool InstallContextMenu(RegistryKey shellRoot)
        //{
        //    var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        //    var file = Path.Combine(directory, "Clowd.exe");
        //    if (!File.Exists(file))
        //        return false;
        //    using (var clowd = shellRoot.CreateSubKey(strContextMenuShellName))
        //    {
        //        clowd.SetValue("Icon", file, RegistryValueKind.String);
        //        using (var command = clowd.CreateSubKey("command"))
        //        {
        //            command.SetValue("", file);
        //        }
        //    }

        //    return true;
        //}
        //private static bool UninstallContextMenu(RegistryKey shellRoot)
        //{
        //    shellRoot.DeleteSubKeyTree(strContextMenuShellName, false);
        //    return true;
        //}
        private static bool InstallComType(Type type, RegistryKey classRoot)
        {
            Assembly assy = Assembly.GetExecutingAssembly();
            string strAsmName = assy.FullName;
            string strAsmCodeBase = assy.CodeBase;
            string strAsmVersion = assy.GetName().Version.ToString();
            string strRuntimeVersion = assy.ImageRuntimeVersion;

            RegisterManagedType(classRoot, type, strAsmName, strAsmVersion, strAsmCodeBase, strRuntimeVersion);
            CallUserDefinedRegistrationMethod(type, true);

            return true;
        }
        private static bool UninstallComType(Type type, RegistryKey classRoot)
        {
            Assembly assy = Assembly.GetExecutingAssembly();
            string strAsmVersion = assy.GetName().Version.ToString();

            UnregisterManagedType(classRoot, type, strAsmVersion);
            CallUserDefinedRegistrationMethod(type, false);

            return true;
        }

        [System.Security.SecurityCritical]
        [ResourceExposure(ResourceScope.Machine)]
        [ResourceConsumption(ResourceScope.Machine)]
        private static void RegisterManagedType(RegistryKey classRoot, Type type, String strAsmName, String strAsmVersion, String strAsmCodeBase, String strRuntimeVersion)
        {
            String strDocString = type.FullName;
            String strClsId = "{" + Marshal.GenerateGuidForType(type).ToString().ToUpper(CultureInfo.InvariantCulture) + "}";
            String strProgId = Marshal.GenerateProgIdForType(type);

            if (strProgId != String.Empty)
            {
                // Create the HKEY_CLASS_ROOT\<wzProgId> key.
                using (RegistryKey TypeNameKey = classRoot.CreateSubKey(strProgId))
                {
                    TypeNameKey.SetValue("", strDocString);

                    // Create the HKEY_CLASS_ROOT\<wzProgId>\CLSID key.
                    using (RegistryKey ProgIdClsIdKey = TypeNameKey.CreateSubKey("CLSID"))
                    {
                        ProgIdClsIdKey.SetValue("", strClsId);
                    }
                }
            }

            // Create the HKEY_CLASS_ROOT\CLSID key.
            using (RegistryKey ClsIdRootKey = classRoot.CreateSubKey("CLSID"))
            {
                // Create the HKEY_CLASS_ROOT\CLSID\<CLSID> key.
                using (RegistryKey ClsIdKey = ClsIdRootKey.CreateSubKey(strClsId))
                {
                    ClsIdKey.SetValue("", strDocString);

                    // Create the HKEY_CLASS_ROOT\CLSID\<CLSID>\InprocServer32 key.
                    using (RegistryKey InProcServerKey = ClsIdKey.CreateSubKey("InprocServer32"))
                    {
                        InProcServerKey.SetValue("", "mscoree.dll");
                        InProcServerKey.SetValue("ThreadingModel", "Both");
                        InProcServerKey.SetValue("Class", type.FullName);
                        InProcServerKey.SetValue("Assembly", strAsmName);
                        InProcServerKey.SetValue("RuntimeVersion", strRuntimeVersion);
                        if (strAsmCodeBase != null)
                            InProcServerKey.SetValue("CodeBase", strAsmCodeBase);

                        // Create the HKEY_CLASS_ROOT\CLSID\<CLSID>\InprocServer32\<Version> subkey
                        using (RegistryKey VersionSubKey = InProcServerKey.CreateSubKey(strAsmVersion))
                        {
                            VersionSubKey.SetValue("Class", type.FullName);
                            VersionSubKey.SetValue("Assembly", strAsmName);
                            VersionSubKey.SetValue("RuntimeVersion", strRuntimeVersion);
                            if (strAsmCodeBase != null)
                                VersionSubKey.SetValue("CodeBase", strAsmCodeBase);
                        }

                        if (strProgId != String.Empty)
                        {
                            // Create the HKEY_CLASS_ROOT\CLSID\<CLSID>\ProdId key.
                            using (RegistryKey ProgIdKey = ClsIdKey.CreateSubKey("ProgId"))
                            {
                                ProgIdKey.SetValue("", strProgId);
                            }
                        }
                    }

                    // Create the HKEY_CLASS_ROOT\CLSID\<CLSID>\Implemented Categories\<Managed Category Guid> key.
                    using (RegistryKey CategoryKey = ClsIdKey.CreateSubKey(strImplementedCategoriesSubKey))
                    {
                        using (RegistryKey ManagedCategoryKey = CategoryKey.CreateSubKey(strManagedCategoryGuid)) { }
                    }
                }
            }
        }
        [System.Security.SecurityCritical]
        [ResourceExposure(ResourceScope.None)]
        [ResourceConsumption(ResourceScope.Machine, ResourceScope.Machine)]
        private static bool UnregisterManagedType(RegistryKey classRoot, Type type, String strAsmVersion)
        {
            bool bAllVersionsGone = true;

            String strClsId = "{" + Marshal.GenerateGuidForType(type).ToString().ToUpper(CultureInfo.InvariantCulture) + "}";
            String strProgId = Marshal.GenerateProgIdForType(type);

            using (RegistryKey ClsIdRootKey = classRoot.OpenSubKey("CLSID", true))
            {
                if (ClsIdRootKey != null)
                {
                    //
                    // Remove the entries under HKEY_CLASS_ROOT\CLSID\<CLSID> key.
                    //

                    using (RegistryKey ClsIdKey = ClsIdRootKey.OpenSubKey(strClsId, true))
                    {
                        if (ClsIdKey != null)
                        {
                            //
                            // Remove the entries in the HKEY_CLASS_ROOT\CLSID\<CLSID>\InprocServer32 key.
                            //

                            using (RegistryKey InProcServerKey = ClsIdKey.OpenSubKey("InprocServer32", true))
                            {
                                if (InProcServerKey != null)
                                {
                                    //
                                    // Remove the entries in HKEY_CLASS_ROOT\CLSID\<CLSID>\InprocServer32\<Version>
                                    //

                                    using (RegistryKey VersionSubKey = InProcServerKey.OpenSubKey(strAsmVersion, true))
                                    {
                                        if (VersionSubKey != null)
                                        {
                                            // Delete the values we created
                                            VersionSubKey.DeleteValue("Assembly", false);
                                            VersionSubKey.DeleteValue("Class", false);
                                            VersionSubKey.DeleteValue("RuntimeVersion", false);
                                            VersionSubKey.DeleteValue("CodeBase", false);

                                            // If there are no other values or subkeys then we can delete the VersionSubKey.
                                            if ((VersionSubKey.SubKeyCount == 0) && (VersionSubKey.ValueCount == 0))
                                                InProcServerKey.DeleteSubKey(strAsmVersion);
                                        }
                                    }

                                    // If there are sub keys left then there are versions left.
                                    if (InProcServerKey.SubKeyCount != 0)
                                        bAllVersionsGone = false;

                                    // If there are no versions left, then delete the threading model and default value.
                                    if (bAllVersionsGone)
                                    {
                                        InProcServerKey.DeleteValue("", false);
                                        InProcServerKey.DeleteValue("ThreadingModel", false);
                                    }

                                    InProcServerKey.DeleteValue("Assembly", false);
                                    InProcServerKey.DeleteValue("Class", false);
                                    InProcServerKey.DeleteValue("RuntimeVersion", false);
                                    InProcServerKey.DeleteValue("CodeBase", false);

                                    // If there are no other values or subkeys then we can delete the InProcServerKey.
                                    if ((InProcServerKey.SubKeyCount == 0) && (InProcServerKey.ValueCount == 0))
                                        ClsIdKey.DeleteSubKey("InprocServer32");
                                }
                            }

                            // remove HKEY_CLASS_ROOT\CLSID\<CLSID>\ProgId
                            // and HKEY_CLASS_ROOT\CLSID\<CLSID>\Implemented Category
                            // only when all versions are removed
                            if (bAllVersionsGone)
                            {
                                // Delete the value we created.
                                ClsIdKey.DeleteValue("", false);

                                if (strProgId != String.Empty)
                                {
                                    //
                                    // Remove the entries in the HKEY_CLASS_ROOT\CLSID\<CLSID>\ProgId key.
                                    //

                                    using (RegistryKey ProgIdKey = ClsIdKey.OpenSubKey("ProgId", true))
                                    {
                                        if (ProgIdKey != null)
                                        {
                                            // Delete the value we created.
                                            ProgIdKey.DeleteValue("", false);

                                            // If there are no other values or subkeys then we can delete the ProgIdSubKey.
                                            if ((ProgIdKey.SubKeyCount == 0) && (ProgIdKey.ValueCount == 0))
                                                ClsIdKey.DeleteSubKey("ProgId");
                                        }
                                    }
                                }


                                //
                                // Remove entries in the  HKEY_CLASS_ROOT\CLSID\<CLSID>\Implemented Categories\<Managed Category Guid> key.
                                //

                                using (RegistryKey CategoryKey = ClsIdKey.OpenSubKey(strImplementedCategoriesSubKey, true))
                                {
                                    if (CategoryKey != null)
                                    {
                                        using (RegistryKey ManagedCategoryKey = CategoryKey.OpenSubKey(strManagedCategoryGuid, true))
                                        {
                                            if (ManagedCategoryKey != null)
                                            {
                                                // If there are no other values or subkeys then we can delete the ManagedCategoryKey.
                                                if ((ManagedCategoryKey.SubKeyCount == 0) && (ManagedCategoryKey.ValueCount == 0))
                                                    CategoryKey.DeleteSubKey(strManagedCategoryGuid);
                                            }
                                        }

                                        // If there are no other values or subkeys then we can delete the CategoryKey.
                                        if ((CategoryKey.SubKeyCount == 0) && (CategoryKey.ValueCount == 0))
                                            ClsIdKey.DeleteSubKey(strImplementedCategoriesSubKey);
                                    }
                                }
                            }

                            // If there are no other values or subkeys then we can delete the ClsIdKey.
                            if ((ClsIdKey.SubKeyCount == 0) && (ClsIdKey.ValueCount == 0))
                                ClsIdRootKey.DeleteSubKey(strClsId);
                        }
                    }

                    // If there are no other values or subkeys then we can delete the CLSID key.
                    if ((ClsIdRootKey.SubKeyCount == 0) && (ClsIdRootKey.ValueCount == 0))
                        classRoot.DeleteSubKey("CLSID");
                }


                //
                // Remove the entries under HKEY_CLASS_ROOT\<wzProgId> key.
                //

                if (bAllVersionsGone)
                {
                    if (strProgId != String.Empty)
                    {
                        using (RegistryKey TypeNameKey = classRoot.OpenSubKey(strProgId, true))
                        {
                            if (TypeNameKey != null)
                            {
                                // Delete the values we created.
                                TypeNameKey.DeleteValue("", false);


                                //
                                // Remove the entries in the HKEY_CLASS_ROOT\<wzProgId>\CLSID key.
                                //

                                using (RegistryKey ProgIdClsIdKey = TypeNameKey.OpenSubKey("CLSID", true))
                                {
                                    if (ProgIdClsIdKey != null)
                                    {
                                        // Delete the values we created.
                                        ProgIdClsIdKey.DeleteValue("", false);

                                        // If there are no other values or subkeys then we can delete the ProgIdClsIdKey.
                                        if ((ProgIdClsIdKey.SubKeyCount == 0) && (ProgIdClsIdKey.ValueCount == 0))
                                            TypeNameKey.DeleteSubKey("CLSID");
                                    }
                                }

                                // If there are no other values or subkeys then we can delete the TypeNameKey.
                                if ((TypeNameKey.SubKeyCount == 0) && (TypeNameKey.ValueCount == 0))
                                    classRoot.DeleteSubKey(strProgId);
                            }
                        }
                    }
                }
            }

            return bAllVersionsGone;
        }
        [System.Security.SecurityCritical]  // auto-generated
        private static void CallUserDefinedRegistrationMethod(Type type, bool bRegister)
        {
            bool bFunctionCalled = false;

            // Retrieve the attribute type to use to determine if a function is the requested user defined
            // registration function.
            Type RegFuncAttrType = null;
            if (bRegister)
                RegFuncAttrType = typeof(ComRegisterFunctionAttribute);
            else
                RegFuncAttrType = typeof(ComUnregisterFunctionAttribute);

            for (Type currType = type; !bFunctionCalled && currType != null; currType = currType.BaseType)
            {
                // Retrieve all the methods.
                MethodInfo[] aMethods = currType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int NumMethods = aMethods.Length;

                // Go through all the methods and check for the ComRegisterMethod custom attribute.
                for (int cMethods = 0; cMethods < NumMethods; cMethods++)
                {
                    MethodInfo CurrentMethod = aMethods[cMethods];

                    // Check to see if the method has the custom attribute.
                    if (CurrentMethod.GetCustomAttributes(RegFuncAttrType, true).Length != 0)
                    {
                        // Check to see if the method is static before we call it.
                        if (!CurrentMethod.IsStatic)
                        {
                            if (bRegister)
                                throw new InvalidOperationException("InvalidOperation_NonStaticComRegFunction " + CurrentMethod.Name + " " + currType.Name);
                            else
                                throw new InvalidOperationException("InvalidOperation_NonStaticComUnRegFunction " + CurrentMethod.Name + " " + currType.Name);
                        }

                        // Finally check that the signature is string ret void.
                        ParameterInfo[] aParams = CurrentMethod.GetParameters();
                        if (CurrentMethod.ReturnType != typeof(void) ||
                            aParams == null ||
                            aParams.Length != 1 ||
                            (aParams[0].ParameterType != typeof(String) && aParams[0].ParameterType != typeof(Type)))
                        {
                            if (bRegister)
                                throw new InvalidOperationException("InvalidOperation_InvalidComRegFunctionSig " + CurrentMethod.Name + " " + currType.Name);
                            else
                                throw new InvalidOperationException("InvalidOperation_InvalidComUnRegFunctionSig " + CurrentMethod.Name + " " + currType.Name);
                        }

                        // There can only be one register and one unregister function per type.
                        if (bFunctionCalled)
                        {
                            if (bRegister)
                                throw new InvalidOperationException("InvalidOperation_MultipleComRegFunctions " + currType.Name);
                            else
                                throw new InvalidOperationException("InvalidOperation_MultipleComUnRegFunctions " + currType.Name);
                        }

                        // The function is valid so set up the arguments to call it.
                        Object[] objs = new Object[1];
                        if (aParams[0].ParameterType == typeof(String))
                        {
                            // We are dealing with the string overload of the function.
                            objs[0] = "HKEY_CLASSES_ROOT\\CLSID\\{" + Marshal.GenerateGuidForType(type).ToString().ToUpper(CultureInfo.InvariantCulture) + "}";
                        }
                        else
                        {
                            // We are dealing with the type overload of the function.
                            objs[0] = type;
                        }

                        // Invoke the COM register function.
                        CurrentMethod.Invoke(null, objs);

                        // Mark the function as having been called.
                        bFunctionCalled = true;
                    }
                }
            }
        }

    }
}
