using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    public sealed unsafe class InfraTests
    {
        private static InteropStatus Selftest(SelftestKind kind, out long value)
        {
            long v = 0;
            int status = NativeMethods.prisc_interop_selftest((int)kind, &v);
            value = v;
            return (InteropStatus)status;
        }

        public static IEnumerable<object[]> Kinds() =>
        [
            [SelftestKind.Ok, InteropStatus.Ok],
            [SelftestKind.None, InteropStatus.None],
            [SelftestKind.Timeout, InteropStatus.Timeout],
            [SelftestKind.Closed, InteropStatus.Closed],
            [SelftestKind.Unsupported, InteropStatus.Unsupported],
            [SelftestKind.InvalidArg, InteropStatus.InvalidArg],
            [SelftestKind.Os, InteropStatus.Os],
            [SelftestKind.Panic, InteropStatus.Panic],
            [SelftestKind.State, InteropStatus.State],
            [SelftestKind.Permission, InteropStatus.Permission],
            [SelftestKind.Capability, InteropStatus.Capability],
        ];

        [Theory]
        [MemberData(nameof(Kinds))]
        public void Every_status_maps_and_failures_throw_with_the_last_error(SelftestKind kind, InteropStatus expected)
        {
            InteropTestGate.Require();
            int status = NativeMethods.prisc_interop_selftest((int)kind, null);
            Assert.Equal(expected, (InteropStatus)status);
            if (status >= 0)
            {
                Assert.Equal(expected, Native.Check(status, "prisc_interop_selftest"));
                Assert.Equal("", Native.LastErrorMessage());
                return;
            }

            var e = Assert.Throws<InteropException>(() => Native.Check(status, "prisc_interop_selftest"));
            Assert.Equal(expected, e.Status);
            Assert.Equal("prisc_interop_selftest", e.Operation);
            Assert.False(String.IsNullOrEmpty(e.Message));
        }

        [Fact]
        public void Ok_writes_its_value_and_os_failures_carry_the_sign_extended_code()
        {
            InteropTestGate.Require();
            Assert.Equal(InteropStatus.Ok, Selftest(SelftestKind.Ok, out var value));
            Assert.Equal(42, value);
            var status = Selftest(SelftestKind.Os, out _);
            var e = Native.Failure((int)status, "prisc_interop_selftest");
            Assert.Equal(unchecked((int)0x80004005), e.NativeCode);
            Assert.Contains("SelftestOsCall failed (0x80004005)", e.Message);
        }

        [Fact]
        public void A_panic_is_caught_and_names_the_export()
        {
            InteropTestGate.Require();
            Assert.Equal(InteropStatus.Panic, Selftest(SelftestKind.Panic, out _));
            Assert.Contains("prisc_interop_selftest panicked", Native.LastErrorMessage());
        }

        [Fact]
        public void A_long_message_is_read_whole()
        {
            InteropTestGate.Require();
            Assert.Equal(InteropStatus.State, Selftest(SelftestKind.LongMessage, out _));
            Assert.True(Native.LastErrorMessage().Length > 1024);
        }

        [Fact]
        public void A_nested_entry_keeps_the_outer_error()
        {
            InteropTestGate.Require();
            Assert.Equal(InteropStatus.State, Selftest(SelftestKind.Nested, out _));
            Assert.Equal("outer", Native.LastErrorMessage());
        }

        [Fact]
        public void Owned_strings_are_taken_once()
        {
            InteropTestGate.Require();
            PriscString s = default;
            Assert.Equal(0, NativeMethods.prisc_interop_version(&s));
            Assert.NotEqual(0u, (uint)s.len);
            var text = Utf8.Take(ref s);
            Assert.False(String.IsNullOrEmpty(text));
            Assert.True(s.ptr == null);
            Assert.Equal("", Utf8.Take(ref s));
            Assert.Equal((int)InteropStatus.InvalidArg, NativeMethods.prisc_string_free(null));
        }

        [Fact]
        public void The_log_sink_receives_every_level()
        {
            InteropTestGate.Require();
            var lines = new List<(InteropLogLevel, string)>();
            InteropLog.SetSink((level, text) =>
            {
                lock (lines)
                    lines.Add((level, text));
            });
            try
            {
                Assert.Equal(InteropStatus.Ok, Selftest(SelftestKind.Log, out _));
            }
            finally
            {
                InteropLog.SetSink(null);
            }

            lock (lines)
            {
                Assert.Contains((InteropLogLevel.Error, "selftest: error"), lines);
                Assert.Contains((InteropLogLevel.Warn, "selftest: warn"), lines);
                Assert.Contains((InteropLogLevel.Info, "selftest: info"), lines);
                Assert.Contains((InteropLogLevel.Debug, "selftest: debug"), lines);
            }
        }

        [Fact]
        public void Stub_exports_answer_unsupported()
        {
            InteropTestGate.Require();
            // (the desktop area's prisc_screens_list, which this used to call, is implemented)
            PriscH264Quality quality;
            Assert.Equal((int)InteropStatus.Unsupported, NativeMethods.prisc_encoding_h264_quality(23, 1920, 1080, 30, 1, &quality));
            Assert.Contains("not implemented yet", Native.LastErrorMessage());
        }

        /// <summary>The public enums are generated from the Rust ones; these literals are the wire
        /// contract (README A4), so a renumbering on either side fails here.</summary>
        [Fact]
        public void The_status_enum_is_the_wire_contract()
        {
            Assert.Equal(0, (int)InteropStatus.Ok);
            Assert.Equal(1, (int)InteropStatus.None);
            Assert.Equal(2, (int)InteropStatus.Timeout);
            Assert.Equal(3, (int)InteropStatus.Closed);
            Assert.Equal(-1, (int)InteropStatus.Unsupported);
            Assert.Equal(-2, (int)InteropStatus.InvalidArg);
            Assert.Equal(-3, (int)InteropStatus.Os);
            Assert.Equal(-4, (int)InteropStatus.Panic);
            Assert.Equal(-5, (int)InteropStatus.State);
            Assert.Equal(-6, (int)InteropStatus.Permission);
            Assert.Equal(-7, (int)InteropStatus.Capability);
            Assert.Equal(11, Enum.GetValues<InteropStatus>().Length);
            Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(InteropStatus)));
            Assert.True(typeof(OverlayFlags).IsDefined(typeof(FlagsAttribute), false));
            Assert.True(typeof(GpuDeviceFlags).IsDefined(typeof(FlagsAttribute), false));
            Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(OverlayFlags)));
        }

        /// <summary>Sizes and offsets the Rust layout tests pin (README B4).</summary>
        [Fact]
        public void Struct_layouts_match_the_rust_pins()
        {
            Assert.Equal(16, sizeof(PriscString));
            Assert.Equal(8, (int)Marshal.OffsetOf<PriscString>(nameof(PriscString.len)));
            Assert.Equal(16, sizeof(PriscRect));
            Assert.Equal(8, sizeof(PriscPoint));
            Assert.Equal(64, sizeof(PriscScreen));
            Assert.Equal(56, (int)Marshal.OffsetOf<PriscScreen>(nameof(PriscScreen.is_primary)));
            Assert.Equal(32, sizeof(PriscScreenImage));
            Assert.Equal(48, sizeof(PriscDevice));
            Assert.Equal(8, sizeof(PriscAudioOutputInfo));
            Assert.Equal(24, sizeof(PriscAudioInputEvent));
            Assert.Equal(32, sizeof(PriscAudioInputInfo));
            Assert.Equal(64, sizeof(PriscGpuDeviceInfo));
            Assert.Equal(24, sizeof(PriscGpuRingInfo));
            Assert.Equal(48, sizeof(PriscHotkeysEvent));
            Assert.Equal(32, (int)Marshal.OffsetOf<PriscHotkeysEvent>(nameof(PriscHotkeysEvent.reason)));
            Assert.Equal(8, sizeof(PriscGesture));
            Assert.Equal(24, sizeof(PriscOkLch));
            Assert.Equal(136, sizeof(PriscAccentPalette));
            Assert.Equal(16, sizeof(PriscH264Quality));
        }

        [Fact]
        public void The_generated_abi_hash_is_what_the_library_reports()
        {
            InteropTestGate.Require();
            ulong hash = 0;
            Assert.Equal(0, NativeMethods.prisc_interop_abi_hash(&hash));
            Assert.Equal(InteropAbi.Hash, hash);
        }
    }
}
