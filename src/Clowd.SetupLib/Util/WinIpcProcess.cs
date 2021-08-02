using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Clowd.Setup.Util
{
    public static class WinIpcProcess
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateNamedPipe(
          String pipeName,
          uint dwOpenMode,
          uint dwPipeMode,
          uint nMaxInstances,
          uint nOutBufferSize,
          uint nInBufferSize,
          uint nDefaultTimeOut,
          IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ConnectNamedPipe(
           SafeFileHandle hNamedPipe,
           IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
           String pipeName,
           uint dwDesiredAccess,
           uint dwShareMode,
           IntPtr lpSecurityAttributes,
           uint dwCreationDisposition,
           uint dwFlagsAndAttributes,
           IntPtr hTemplate);

        private const uint WRITE_ONLY = (0x00000002);
        private const uint FILE_FLAG_OVERLAPPED = (0x40000000);
        private const uint GENERIC_READ = (0x80000000);
        private const uint OPEN_EXISTING = 3;
        private const uint ERROR_PIPE_CONNECTED = 535;
        private const uint BUFFER_SIZE = 4096;

        private class State
        {
            public readonly EventWaitHandle eventWaitHandle;
            public int result { get; set; }
            public SafeFileHandle clientPipeHandle { get; set; }

            public State()
            {
                eventWaitHandle = new ManualResetEvent(false);
            }
        }

        private static string GetPipeName(string syncProcessName)
        {
            return string.Format("\\\\.\\pipe\\{0}", syncProcessName);
        }

        public static Process LaunchProcessAndSend<T>(T dto, ProcessStartInfo processStartInfo, string pipeName)
        {
            Process p;
            State state = new State();

            using (state.clientPipeHandle = CreateNamedPipe(
                   GetPipeName(pipeName),
                   WRITE_ONLY | FILE_FLAG_OVERLAPPED,
                   0,
                   1, // 1 max instance (only the updater utility is expected to connect)
                   BUFFER_SIZE,
                   BUFFER_SIZE,
                   0,
                   IntPtr.Zero))
            {
                if (state.clientPipeHandle.IsInvalid)
                    throw new Exception("Launch process client: Failed to create named pipe, handle is invalid.");

                // This will throw Win32Exception if the user denies UAC
                p = Process.Start(processStartInfo);

                ThreadPool.QueueUserWorkItem(ConnectChildPipe, state);
                state.eventWaitHandle.WaitOne(10000);

                //failed to connect client pipe
                if (state.result == 0)
                    throw new Exception("Launch process client: Failed to connect to named pipe");

                //client connection successful
                using (var fStream = new FileStream(state.clientPipeHandle, FileAccess.Write, (int)BUFFER_SIZE, true))
                {
                    new BinaryFormatter().Serialize(fStream, dto);
                    fStream.Flush();
                    fStream.Close();
                }
            }

            return p;
        }

        public static T ConnectAndRead<T>(string pipeName)
        {
            return (T)ReadDto(pipeName);
        }

        private static void ConnectChildPipe(object stateObject)
        {
            if (stateObject == null) return;
            State state = (State)stateObject;

            try
            {
                state.result = ConnectNamedPipe(state.clientPipeHandle, IntPtr.Zero);
            }
            catch { }
            //Check for the oddball: ERROR - PIPE CONNECTED
            //Ref: http://msdn.microsoft.com/en-us/library/windows/desktop/aa365146%28v=vs.85%29.aspx
            if (Marshal.GetLastWin32Error() == ERROR_PIPE_CONNECTED) { state.result = 1; }
            state.eventWaitHandle.Set(); // signal we're done
        }

        private static object ReadDto(string syncProcessName)
        {
            using (SafeFileHandle pipeHandle = CreateFile(
                GetPipeName(syncProcessName),
                GENERIC_READ,
                0,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero))
            {

                if (pipeHandle.IsInvalid)
                    return null;

                using (var fStream = new FileStream(pipeHandle, FileAccess.Read, (int)BUFFER_SIZE, true))
                {
                    return new BinaryFormatter().Deserialize(fStream);
                }
            }
        }
    }
}
