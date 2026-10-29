using System;
using System.Collections.Generic;
using System.Diagnostics;
using Prisc.Interop;
using Prisc.Ipc;
using Prisc.VideoSDK;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>The wrapper-level items of this area: one success, one documented failure and one
        /// round trip per export family, keyed <c>&lt;area&gt;.&lt;item&gt;</c>. Every export here is
        /// any-thread. The stdout guard is not probed: claiming stdout would take the probe's own, and
        /// it is covered by the child-process tests (prisc_interop tests/ipc_process.rs,
        /// Prisc.Ipc.Tests IpcChildGuardTests) and the VideoRender E2E row.</summary>
        static partial void ProbeIpc(ProbeRun run)
        {
            // the generated protocol constants prisc_ipc owns
            run.Item("ipc.protocol.constants", () => new Dictionary<string, object>
            {
                ["max_line_bytes"] = IpcProtocol.MaxLineBytes,
                ["self_exit_margin_ms"] = IpcProtocol.SelfExitMarginMs,
                ["video_render"] = HelperNames.VideoRender,
            }, anyThread: true);

            // the foreground grant: Windows only, best-effort; a pid of zero or less never reaches it
            run.Item("ipc.foreground.allow_any", () => Foreground.AllowAny(), anyThread: true);
            run.Item("ipc.foreground.allow_refuses_nonpositive", () => !Foreground.Allow(0) && !Foreground.Allow(-1)
                ? true
                : throw new InvalidOperationException("a pid of zero or less reached the grant"), anyThread: true);

            // the kill-on-close job: a short-lived child of our own on Windows; false elsewhere
            run.Item("ipc.job.assign", IpcAssignJob, anyThread: true);
            run.Item("ipc.job.zero_handle", () => IpcExpectFailure(() => KillOnCloseJob.Assign(IntPtr.Zero),
                InteropStatus.InvalidArg), anyThread: true);

            // the package identity (Windows only: unsupported elsewhere, null for a normal launch)
            run.Item("ipc.process.package_full_name", () => ProcessNative.PackageFullName() ?? "none", anyThread: true);

            // the FFmpeg directory beside the recorder: the macOS dylib closure count
            run.Item("ipc.process.prepare_ffmpeg_dir", () => FFmpegLoader.FindBesideRecorder() is { } dir
                ? ProcessNative.PrepareFfmpegDirectory(dir)
                : ProbeOutcome.Unavailable("no recorder payload found"), anyThread: true);
            run.Item("ipc.process.prepare_ffmpeg_dir_nul", () => IpcExpectFailure(() => ProcessNative.PrepareFfmpegDirectory("a\0b"),
                InteropStatus.InvalidArg), anyThread: true);
        }

        private static object IpcAssignJob()
        {
            // off Windows the export answers unsupported without touching the handle
            if (!OperatingSystem.IsWindows())
                return KillOnCloseJob.Assign((IntPtr)Environment.ProcessId);

            using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            try
            {
                return KillOnCloseJob.Assign(child.Handle);
            }
            finally
            {
                try { child?.Kill(); }
                catch (InvalidOperationException) { }
            }
        }

        /// <summary>A call that must fail with <paramref name="expected"/>: ok with the status when it
        /// does, an error when it succeeds or fails differently.</summary>
        private static object IpcExpectFailure(Func<object> call, InteropStatus expected)
        {
            try
            {
                call();
            }
            catch (InteropException e) when (e.Status == expected)
            {
                return e.Status.ToString();
            }
            throw new InvalidOperationException($"expected {expected}");
        }
    }
}
