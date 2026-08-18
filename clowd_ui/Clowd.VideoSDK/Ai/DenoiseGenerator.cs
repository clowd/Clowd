using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Ai
{
    /// <summary>
    /// Generates the denoise sidecar for one audio stream: decode at 48 kHz, stream the PCM
    /// through <c>clowd_tractnni denoise</c>, and write the result as
    /// <c>denoise-{SourceId}-{StreamIndex}.wav</c> (float32, companion json per
    /// <see cref="AiSidecars"/>) beside the project. The wav shares the source stream's timeline —
    /// same sample positions, same length — so <see cref="DenoisedAudioSource"/> can read it at
    /// the raw stream's positions with no mapping.
    ///
    /// <para>The decode rides <see cref="SequentialAudioSource"/> over a throwaway single-source
    /// project rather than a raw decoder loop: its anchor/gap-silence bookkeeping is exactly what
    /// keeps the sidecar's sample positions identical to what the mixer reads from the raw stream.
    /// The channel count sent to the model is <c>min(source channels, 2)</c> — a mono recording is
    /// denoised once, not twice — with the decoder's fixed stereo downmixed by
    /// <see cref="DownmixInto"/>, which inverts the decoder's mono→stereo rematrix gain.</para>
    ///
    /// <para>The stdin write runs on its own task while this thread drains stdout (see
    /// <see cref="TractnniClient"/> for why), spooling into a temp file that becomes the wav in
    /// one atomic move. The companion json is written only after the wav is in place, so a valid
    /// companion always implies a complete sidecar.</para>
    /// </summary>
    public static class DenoiseGenerator
    {
        /// <summary>The DSP contract's fixed rate: the model is trained at 48 kHz.</summary>
        public const int SampleRate = 48000;

        internal const int WavHeaderBytes = 44;

        /// <summary>100ms of decode per pipe write — big enough to amortize the syscalls, small
        /// enough that cancellation lands quickly.</summary>
        private const int ChunkFrames = SampleRate / 10;

        /// <summary>
        /// Generates the sidecar for (<paramref name="source"/>, <paramref name="streamIndex"/>),
        /// replacing any existing one. Returns false without doing anything when there is nowhere
        /// to cache (<paramref name="cacheDir"/> null — the dev harness) or no
        /// <c>clowd_tractnni</c> binary resolves; throws on decode or inference failure (with the
        /// process's stderr tail), <see cref="NotSupportedException"/> before any inference when
        /// the stream is too long for the wav's 32-bit RIFF size (~3.1 h at stereo 48 kHz), and
        /// <see cref="OperationCanceledException"/> on cancellation. Progress is 0..1.
        /// </summary>
        public static bool Generate(Source source, int streamIndex, string cacheDir,
            IProgress<double> progress = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (String.IsNullOrEmpty(cacheDir))
                return false;

            var exe = TractnniLoader.TryGetPath();
            if (exe == null)
                return false;

            int channels;
            using (var probe = new SyncAudioStreamDecoder(source.Path, streamIndex, SampleRate))
                channels = Math.Clamp(probe.SourceChannelCount, 1, 2);

            long expectedFrames = 0;
            foreach (var stream in source.Streams ?? [])
            {
                if (stream.Index == streamIndex && stream.DurationTicks > 0)
                    expectedFrames = AudioTime.SamplesCeil(stream.DurationTicks, SampleRate);
            }

            int blockAlign = channels * 4;
            if (expectedFrames > MaxWavFrames(channels))
            {
                var cap = TimeSpan.FromSeconds(MaxWavFrames(channels) / (double)SampleRate);
                throw new NotSupportedException(
                    $"This stream is too long to denoise — the sidecar wav format caps at 4 GB, " +
                    $"about {cap.TotalHours:0.0} hours at {channels}ch 48 kHz.");
            }

            var wavPath = AiSidecars.DenoisePath(cacheDir, source.Id, streamIndex);
            Directory.CreateDirectory(cacheDir);
            var temp = wavPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            using var client = TractnniClient.Start(exe, new[]
            {
                "denoise", "--channels", channels.ToString(CultureInfo.InvariantCulture),
            });
            using var killOnCancel = cancellationToken.Register(client.Kill);

            // decode → stdin on its own task; this thread drains stdout. CloseInput runs in the
            // pump's finally either way, so the process always sees EOF and the drain below always
            // terminates — a decode failure surfaces after the exit code has told its story.
            var pump = Task.Run(() => PumpInput(client, source, streamIndex, channels, cancellationToken));

            long framesReceived;
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    stream.Position = WavHeaderBytes; // reserved; patched once the length is known

                    var buffer = new byte[64 * 1024];
                    long bytes = 0;
                    int read;
                    while ((read = client.Output.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        stream.Write(buffer, 0, read);
                        bytes += read;
                        if (progress != null && expectedFrames > 0)
                            progress.Report(Math.Min(0.99, bytes / (double)(expectedFrames * blockAlign)));
                    }

                    Exception pumpError = null;
                    long framesSent = 0;
                    try { framesSent = pump.GetAwaiter().GetResult(); }
                    catch (Exception ex) { pumpError = ex; }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!client.WaitForExit(10_000))
                    {
                        client.Kill();
                        throw new InvalidOperationException(
                            "clowd_tractnni did not exit after its output ended." + StderrSuffix(client));
                    }
                    client.ThrowIfFailed();
                    if (pumpError != null)
                        throw new InvalidOperationException("Decoding the source audio failed.", pumpError);

                    framesReceived = bytes / blockAlign;
                    if (bytes % blockAlign != 0 || framesReceived != framesSent)
                    {
                        throw new InvalidOperationException(
                            $"clowd_tractnni returned {bytes} bytes for {framesSent} frames of " +
                            $"{channels}ch input — the streams must match 1:1." + StderrSuffix(client));
                    }

                    stream.Position = 0;
                    WriteWavHeader(stream, channels, SampleRate, checked((uint)(framesReceived * blockAlign)));
                }

                File.Move(temp, wavPath, overwrite: true);
            }
            catch
            {
                client.Kill();
                try { pump.Wait(TimeSpan.FromSeconds(3)); }
                catch { /* its failure is already the story, or was killed */ }
                try { File.Delete(temp); }
                catch { /* best effort */ }
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            AiSidecars.TryWriteCompanion(wavPath, AiSidecars.DescribeSource(source.Path));
            progress?.Report(1.0);
            return true;
        }

        /// <summary>Decodes the stream forward and writes f32le interleaved PCM to the process's
        /// stdin, returning the sample-frame count sent. Always closes stdin, so the process (and
        /// with it the caller's stdout drain) terminates whatever happens here.</summary>
        private static long PumpInput(TractnniClient client, Source source, int streamIndex,
            int channels, CancellationToken cancellationToken)
        {
            try
            {
                // a throwaway single-source project puts SequentialAudioSource's positioning
                // logic (anchor, gap silence) between the decoder and the pipe — the sidecar's
                // timeline must be the raw stream's timeline, not the packet stream's.
                var project = new Project
                {
                    Output = new OutputSettings { SampleRate = SampleRate },
                    Sources = { source },
                };
                using var reader = new SequentialAudioSource(project);

                var stereo = new float[ChunkFrames * AudioMixer.Channels];
                var mono = channels == 1 ? new float[ChunkFrames] : null;
                var bytes = new byte[ChunkFrames * channels * 4];
                long pos = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!reader.ReadSamples(source.Id, streamIndex, pos, stereo, ChunkFrames,
                            out int framesRead) || framesRead <= 0)
                        break;

                    int byteCount;
                    if (channels == 1)
                    {
                        DownmixInto(stereo, mono, framesRead);
                        byteCount = framesRead * 4;
                        Buffer.BlockCopy(mono, 0, bytes, 0, byteCount);
                    }
                    else
                    {
                        byteCount = framesRead * 2 * 4;
                        Buffer.BlockCopy(stereo, 0, bytes, 0, byteCount);
                    }

                    client.Input.Write(bytes, 0, byteCount);
                    pos += framesRead;
                    if (framesRead < ChunkFrames)
                        break; // end of stream
                }

                client.Input.Flush();
                return pos;
            }
            finally
            {
                client.CloseInput();
            }
        }

        /// <summary>Collapses the decoder's fixed stereo back to the source's mono layout. The
        /// decoder's mono→stereo rematrix (swresample's default) puts sqrt(1/2)·M in each channel,
        /// and playback of the mono sidecar goes through the same rematrix again — so a plain
        /// average would leave the denoised branch a uniform 3 dB quiet. Scaling the sum by
        /// sqrt(1/2) inverts the rematrix exactly, storing M itself in the wav.</summary>
        internal static void DownmixInto(float[] stereo, float[] mono, int frames)
        {
            const float InverseRematrix = 0.7071067811865476f; // sqrt(1/2)
            for (int i = 0; i < frames; i++)
                mono[i] = InverseRematrix * (stereo[i * 2] + stereo[i * 2 + 1]);
        }

        /// <summary>The most sample frames a sidecar wav can hold at this layout: the RIFF size
        /// field is 32-bit and counts the data plus 36 header bytes, capping the data chunk at
        /// <c>uint.MaxValue - 36</c> bytes (~3.1 hours of stereo float32 at 48 kHz).</summary>
        internal static long MaxWavFrames(int channels) => (uint.MaxValue - 36L) / (channels * 4L);

        /// <summary>The canonical 44-byte PCM IEEE-float header (format tag 3, 32-bit).</summary>
        internal static void WriteWavHeader(Stream stream, int channels, int sampleRate, uint dataBytes)
        {
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16u);                                  // fmt chunk size
            writer.Write((ushort)3);                            // WAVE_FORMAT_IEEE_FLOAT
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)(sampleRate * channels * 4));    // byte rate
            writer.Write((ushort)(channels * 4));               // block align
            writer.Write((ushort)32);                           // bits per sample
            writer.Write("data"u8);
            writer.Write(dataBytes);
        }

        private static string StderrSuffix(TractnniClient client)
        {
            var tail = client.StderrTail;
            return tail.Length > 0 ? Environment.NewLine + tail : "";
        }
    }
}
