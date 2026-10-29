using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Prisc.PlatformUtil;
using Prisc.Util;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The session.json contract between the Rust capture helpers that write it
    /// (<c>prisc_rust_core::session::SessionJson</c>) and the shell that reads it
    /// (<see cref="SessionInfo"/>). <see cref="SessionContract.RustSample"/> is a file the Rust
    /// writer's own serde shape produced with every field set (<c>cargo codegen</c> regenerates it
    /// from <c>session::contract_sample()</c>); it is read here the way
    /// <c>FileSyncObject.PopulateFromJson</c> reads a session from disk: property by property
    /// through <see cref="PriscUiJsonContext"/>. A field renamed or retyped on either side stops
    /// arriving, and this fails.
    /// </summary>
    public class SessionContractTests
    {
        private static object Read(string field)
        {
            var typeInfo = PriscUiJsonContext.Default.SessionInfo;
            var property = typeInfo.Properties.SingleOrDefault(p => p.Name == field);
            Assert.True(property != null, $"SessionInfo has no JSON property named {field}");
            Assert.True(property.Set != null, $"SessionInfo.{field} cannot be set from session.json");

            var sample = JsonNode.Parse(SessionContract.RustSample).AsObject();
            return sample[field].Deserialize(property.PropertyType, typeInfo.Options);
        }

        [Fact]
        public void The_field_list_is_the_samples_own()
        {
            var sample = JsonNode.Parse(SessionContract.RustSample).AsObject();
            Assert.Equal(SessionContract.RustFields.OrderBy(f => f, StringComparer.Ordinal),
                sample.Select(p => p.Key).OrderBy(f => f, StringComparer.Ordinal));
        }

        [Fact]
        public void Every_field_the_rust_writer_sets_arrives_with_its_value()
        {
            foreach (var field in SessionContract.RustFields)
            {
                var value = Read(field);
                Assert.True(value != null, $"{field} arrived as null");
                if (value.GetType().IsValueType)
                    Assert.NotEqual(Activator.CreateInstance(value.GetType()), value);
            }

            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), ((DateTime)Read("CreatedUtc")).ToUniversalTime());
            Assert.Equal("Screenshot", Read("Name"));
            Assert.Equal("/sessions/1/desktop.png", Read("DesktopImgPath"));
            Assert.Equal("/sessions/1/cropped.png", Read("PreviewImgPath"));
            Assert.Equal("/sessions/1/cursor.png", Read("CursorImgPath"));
            Assert.Equal(new ScreenRect(11, 12, 32, 32), Read("CursorPosition"));
            Assert.Equal(new ScreenRect(-20, 30, 640, 480), Read("CroppedRect"));
            Assert.Equal(new ScreenRect(-100, 0, 2560, 1440), Read("OriginalBounds"));
            Assert.Equal(16.0, Read("CornerRadius"));
        }
    }
}
