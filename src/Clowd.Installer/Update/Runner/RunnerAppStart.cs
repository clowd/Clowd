using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Clowd.Installer;
using NAppUpdate.Framework;
using NAppUpdate.Framework.Common;
using NAppUpdate.Framework.Tasks;
using NAppUpdate.Framework.Utils;

namespace NAppUpdate.Updater
{
    internal static class RunnerAppStart
    {
        private static InstallerArgs _args;
        private static Logger _logger;
        //private static ConsoleForm _console;

        public static void Run(InstallerArgs args)
        {
            //Debugger.Launch();
            _args = args;
            string tempFolder = string.Empty;
            string logFile = string.Empty;

            _logger = UpdateManager.Instance.Logger;

            //if (_args.ShowConsole)
            //{
            //    _console = new ConsoleForm();
            //    _console.Show();
            //}

            Log("Starting to process cold updates...");

            var workingDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            //if (_args.LogTo)
            //{
            //    // Setup a temporary location for the log file, until we can get the DTO
            //    logFile = Path.Combine(workingDir, @"NauUpdate.log");
            //}

            try
            {
                // Get the update process name, to be used to create a named pipe and to wait on the application
                // to quit
                string syncProcessName = Clowd.Constants.UpdateProcessName;
                if (string.IsNullOrEmpty(syncProcessName)) //Application.Exit();
                    throw new ArgumentException("The command line needs to specify the mutex of the program to update.", "ar" + "gs");

                Log("Update process name: '{0}'", syncProcessName);

                // Load extra assemblies to the app domain, if present
                var availableAssemblies = FileSystem.GetFiles(workingDir, "*.exe|*.dll", SearchOption.TopDirectoryOnly);
                foreach (var assemblyPath in availableAssemblies)
                {
                    Log("Loading {0}", assemblyPath);

                    if (assemblyPath.Equals(Assembly.GetEntryAssembly().Location, StringComparison.InvariantCultureIgnoreCase) || assemblyPath.EndsWith("NAppUpdate.Framework.dll"))
                    {
                        Log("\tSkipping (part of current execution)");
                        continue;
                    }

                    try
                    {
                        // ReSharper disable UnusedVariable
                        var assembly = Assembly.LoadFile(assemblyPath);
                        // ReSharper restore UnusedVariable
                    }
                    catch (BadImageFormatException ex)
                    {
                        Log("\tSkipping due to an error: {0}", ex.Message);
                    }
                }

                // Connect to the named pipe and retrieve the updates list
                var dto = NauIpc.ReadDto(syncProcessName) as NauIpc.NauDto;

                // Make sure we start updating only once the application has completely terminated
                Thread.Sleep(1000); // Let's even wait a bit
                bool createdNew;
                using (var mutex = new Mutex(false, syncProcessName + "Mutex", out createdNew))
                {
                    try
                    {
                        if (!createdNew) mutex.WaitOne();
                    }
                    catch (AbandonedMutexException)
                    {
                        // An abandoned mutex is exactly what we are expecting...
                    }
                    finally
                    {
                        Log("The application has terminated (as expected)");
                    }
                }

                bool updateSuccessful = true;

                if (dto == null || dto.Configs == null) throw new Exception("Invalid DTO received");

                if (dto.LogItems != null) // shouldn't really happen
                    _logger.LogItems.InsertRange(0, dto.LogItems);
                dto.LogItems = _logger.LogItems;

                // Get some required environment variables
                string appPath = dto.AppPath;
                string appDir = dto.WorkingDirectory ?? Path.GetDirectoryName(appPath) ?? string.Empty;
                tempFolder = dto.Configs.TempFolder;
                string backupFolder = dto.Configs.BackupFolder;
                bool relaunchApp = dto.RelaunchApplication;

                if (!string.IsNullOrEmpty(dto.AppPath)) logFile = Path.Combine(Path.GetDirectoryName(dto.AppPath), @"NauUpdate.log"); // now we can log to a more accessible location

                if (dto.Tasks == null || dto.Tasks.Count == 0) throw new Exception("Could not find the updates list (or it was empty).");

                Log("Got {0} task objects", dto.Tasks.Count);

                //This can be handy if you're trying to debug the updater.exe!
                //#if (DEBUG)
                //{
                //    if (_args.ShowConsole)
                //    {
                //        _console.WriteLine();
                //        _console.WriteLine("Pausing to attach debugger.  Press any key to continue.");
                //        _console.ReadKey();
                //    }

                //}
                //#endif

                _args.AppDirectory = appDir;
                _args.Startup();

                // Perform the actual off-line update process
                foreach (var t in dto.Tasks)
                {
                    Log("Task \"{0}\": {1}", t.Description, t.ExecutionStatus);

                    if (t.ExecutionStatus != TaskExecutionStatus.RequiresAppRestart && t.ExecutionStatus != TaskExecutionStatus.RequiresPrivilegedAppRestart)
                    {
                        Log("\tSkipping");
                        continue;
                    }

                    Log("\tExecuting...");

                    // TODO: Better handling on failure: logging, rollbacks
                    try
                    {
                        t.ExecutionStatus = t.Execute(true);
                    }
                    catch (Exception ex)
                    {
                        Log(ex);
                        updateSuccessful = false;
                        t.ExecutionStatus = TaskExecutionStatus.Failed;
                    }

                    if (t.ExecutionStatus == TaskExecutionStatus.Successful) continue;
                    Log("\tTask execution failed");
                    updateSuccessful = false;
                    break;
                }

                if (updateSuccessful)
                {
                    Log("Finished successfully");
                    Log("Removing backup folder");
                    if (Directory.Exists(backupFolder)) FileSystem.DeleteDirectory(backupFolder);
                }
                else
                {
                    Clowd.PlatformUtil.BuiltInPlatformExtensions.ShowMessageBox(Clowd.PlatformUtil.Platform.Current, "Update failed");
                    Log(Logger.SeverityLevel.Error, "Update failed");
                }

                // Start the application only if requested to do so
                if (relaunchApp)
                {
                    Log("Re-launching process {0} with working dir {1}", appPath, appDir);
                    ProcessStartInfo info;
                    //if (_args.ShowConsole)
                    //{
                    //    info = new ProcessStartInfo
                    //    {
                    //        UseShellExecute = false,
                    //        WorkingDirectory = appDir,
                    //        FileName = appPath,
                    //    };
                    //}
                    //else
                    //{
                    info = new ProcessStartInfo
                    {
                        UseShellExecute = true,
                        WorkingDirectory = appDir,
                        FileName = appPath,
                    };
                    //}

                    try
                    {
                        NauIpc.LaunchProcessAndSendDto(dto, info, syncProcessName);
                    }
                    catch (Exception ex)
                    {
                        throw new UpdateProcessFailedException("Unable to relaunch application and/or send DTO", ex);
                    }
                }

                Log("All done");
                //Application.Exit();
            }
            catch (Exception ex)
            {
                // supressing catch because if at any point we get an error the update has failed
                Log(ex);
            }
            finally
            {
                //if (_args.LogTo)
                //{
                //    // at this stage we can't make any assumptions on correctness of the path
                //    FileSystem.CreateDirectoryStructure(logFile, true);
                //    _logger.Dump(logFile);
                //}

                //if (_args.ShowConsole)
                //{
                //    if (_args.LogTo)
                //    {
                //        _console.WriteLine();
                //        _console.WriteLine("Log file was saved to {0}", logFile);
                //        _console.WriteLine();
                //    }
                //    _console.WriteLine();
                //    _console.WriteLine("Press any key or close this window to exit.");
                //    _console.ReadKey();
                //}
                if (!string.IsNullOrEmpty(tempFolder)) SelfCleanUp(tempFolder);
                Environment.Exit(0);
            }
        }

        private static void SelfCleanUp(string tempFolder)
        {
            // Delete the updater EXE and the temp folder
            Log("Removing updater and temp folder... {0}", tempFolder);
            try
            {
                var info = new ProcessStartInfo
                {
                    Arguments = string.Format(@"/C ping 1.1.1.1 -n 1 -w 3000 > Nul & echo Y|del ""{0}\*.*"" & rmdir ""{0}""", tempFolder),
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    FileName = "cmd.exe"
                };

                Process.Start(info);
            }
            catch
            {
                /* ignore exceptions thrown while trying to clean up */
            }
        }

        private static void Log(string message, params object[] args)
        {
            Clowd.Installer.Log.White(String.Format(message, args));
        }

        private static void Log(Logger.SeverityLevel severity, string message, params object[] args)
        {
            switch (severity)
            {
                case Logger.SeverityLevel.Debug:
                    Clowd.Installer.Log.White(String.Format(message, args));
                    break;
                case Logger.SeverityLevel.Warning:
                    Clowd.Installer.Log.Yellow(String.Format(message, args));
                    break;
                case Logger.SeverityLevel.Error:
                    Clowd.Installer.Log.Red(String.Format(message, args));
                    break;
            }
        }

        private static void Log(Exception ex)
        {
            Clowd.Installer.Log.Red(ex.ToString());
        }
    }
}
