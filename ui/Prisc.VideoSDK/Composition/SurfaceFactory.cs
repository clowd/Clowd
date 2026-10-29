using System;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// Backend selection: tries the GPU once at startup and falls back to CPU automatically.
    /// The SDK carries no logging dependency, so selection is reported through the optional
    /// <c>diagnosticLog</c> callback: hosts wire it to their logger, and the chosen
    /// <see cref="ISurfaceFactory.BackendName"/> should be included in render diagnostics.
    /// </summary>
    public static class SurfaceFactory
    {
        /// <summary>
        /// Creates the surface backend. Must be called on the thread that will own the factory
        /// (see <see cref="ComposerThread"/>, which does this for you).
        /// </summary>
        /// <param name="preferGpu">When true, attempt a headless GPU context first; a failure
        /// (RDP, VM, CI, missing native backend) silently degrades to CPU.</param>
        /// <param name="diagnosticLog">Receives one line per selection decision.</param>
        public static ISurfaceFactory Create(bool preferGpu, Action<string> diagnosticLog = null)
        {
            if (preferGpu)
            {
                var gpu = GpuSurfaceFactory.TryCreate(out var reason);
                if (gpu != null)
                {
                    // The adapter name says which GPU a field report about a GPU problem was on.
                    diagnosticLog?.Invoke(String.IsNullOrEmpty(gpu.GpuName)
                        ? $"Composition surface backend: {gpu.BackendName}"
                        : $"Composition surface backend: {gpu.BackendName} ({gpu.GpuName})");
                    return gpu;
                }

                diagnosticLog?.Invoke($"GPU surface backend unavailable ({reason}); falling back to CPU.");
            }

            var cpu = new CpuSurfaceFactory();
            diagnosticLog?.Invoke($"Composition surface backend: {cpu.BackendName}");
            return cpu;
        }
    }
}
