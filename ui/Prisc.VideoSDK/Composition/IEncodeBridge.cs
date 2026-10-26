using System;
using Clowd.VideoSDK.Media;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The render loop's view of a zero-copy encode path: composed slots of the readback ring
    /// are handed to the encoder in video memory instead of being read back. Platform-neutral
    /// so the loop is written once; today the one implementation is
    /// <see cref="D3D11EncodeBridge"/> (Windows, Direct3D 12 composer → Direct3D 11 encoder
    /// input), and a Metal/IOSurface counterpart would slot in here.
    ///
    /// <para>Per slot, in this order: <see cref="SubmitFrame"/> on the composer thread once the
    /// frame is drawn (replaces <see cref="IReadbackRing.Submit"/>), then on the convert thread
    /// <see cref="Convert"/> with the value it returned, then <see cref="ReleaseSlot"/> — after
    /// which the ring's slot is idle and may be composed into again. The
    /// <see cref="HardwareFrame"/> from <see cref="Convert"/> goes to
    /// <see cref="Mp4Writer.SubmitVideoFrame(HardwareFrame, long)"/> on the encode thread and is
    /// disposed there; the encoder keeps its own reference to the frame's texture until it has
    /// read it, so slot reuse and encoder in-flight depth are decoupled. Dispose after the
    /// writer opened over <see cref="Frames"/> and before the ring.</para>
    /// </summary>
    internal interface IEncodeBridge : IDisposable
    {
        /// <summary>The encoder-facing frames: what <see cref="Mp4WriterOptions.HardwareFrames"/> takes.</summary>
        HardwareFrames Frames { get; }

        /// <summary>Human-readable description for the diagnostic line that names the encode path.</summary>
        string Description { get; }

        /// <summary>Ends drawing on a ring slot for this bridge (composer thread); returns the
        /// fence value <see cref="Convert"/> needs.</summary>
        ulong SubmitFrame(int slot);

        /// <summary>Turns the submitted slot into an encoder frame on the GPU (convert thread).</summary>
        HardwareFrame Convert(int slot, ulong fenceValue);

        /// <summary>Waits until the GPU has finished reading the slot and makes it idle (convert thread).</summary>
        void ReleaseSlot(int slot);
    }
}
