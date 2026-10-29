using System;
using System.IO;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    /// <summary>The IPC and process family: the foreground grant, the kill-on-close job, the FFmpeg
    /// directory preparation and the package identity, through the public wrappers. The stdout guard
    /// takes the process's stdout, so its success path runs in child processes (Prisc.Ipc.Tests
    /// IpcChildGuardTests and prisc_interop's tests/ipc_process.rs); here only its refusals.</summary>
    public sealed class IpcProcessTests
    {
        [Fact]
        public unsafe void The_stdout_guard_refuses_a_null_output_without_claiming()
        {
            InteropTestGate.Require();
            nint handle = 0;
            byte redirected = 0;
            Assert.Equal((int)InteropStatus.InvalidArg, NativeMethods.prisc_ipc_claim_stdout(null, &redirected));
            Assert.Equal((int)InteropStatus.InvalidArg, NativeMethods.prisc_ipc_claim_stdout(&handle, null));
            Assert.Equal(0, handle);
        }

        [Fact]
        public void The_foreground_grant_never_reaches_a_pid_of_zero_or_less()
        {
            InteropTestGate.Require();
            Assert.False(Foreground.Allow(0));
            Assert.False(Foreground.Allow(-1));
            Assert.False(Foreground.Allow(Int32.MinValue));
        }

        [Fact]
        public void The_foreground_grant_is_windows_only()
        {
            InteropTestGate.Require();
            bool any = Foreground.AllowAny();
            bool self = Foreground.Allow(Environment.ProcessId);
            if (!OperatingSystem.IsWindows())
            {
                Assert.False(any);
                Assert.False(self);
            }
            // on Windows either answer is right: it depends on who owns the foreground
        }

        [Fact]
        public void The_kill_on_close_job_refuses_a_zero_handle_and_is_windows_only()
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => KillOnCloseJob.Assign(IntPtr.Zero));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
            if (!OperatingSystem.IsWindows())
                Assert.False(KillOnCloseJob.Assign((IntPtr)Environment.ProcessId));
        }

        [Fact]
        public void The_package_identity_is_none_in_a_test_host_and_unsupported_off_windows()
        {
            InteropTestGate.Require();
            if (OperatingSystem.IsWindows())
            {
                Assert.Null(ProcessNative.PackageFullName());
                return;
            }
            var e = Assert.Throws<InteropException>(() => ProcessNative.PackageFullName());
            Assert.Equal(InteropStatus.Unsupported, e.Status);
        }

        [Fact]
        public void Preparing_a_directory_without_ffmpeg_loads_nothing_and_a_nul_is_refused()
        {
            InteropTestGate.Require();
            var dir = Directory.CreateTempSubdirectory("prisc-noffmpeg-");
            try
            {
                Assert.Equal(0, ProcessNative.PrepareFfmpegDirectory(dir.FullName));
            }
            finally
            {
                dir.Delete(recursive: true);
                // Windows: back to the default DLL search order
                ProcessNative.PrepareFfmpegDirectory(String.Empty);
            }

            var e = Assert.Throws<InteropException>(() => ProcessNative.PrepareFfmpegDirectory("a\0b"));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
        }
    }
}
