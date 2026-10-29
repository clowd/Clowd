using System;
using System.Linq;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Prisc.Interop's public types sit beside Avalonia in the same files (Prisc.Ui imports both), so a
    /// public interop type named like a public type in <c>Avalonia.Controls</c>, <c>Avalonia.Input</c>,
    /// <c>Avalonia.Media</c> or <c>Avalonia.Platform</c> would make every such file ambiguous. This test
    /// sees both assemblies and keeps the names apart as the area work packages add wrappers.
    /// </summary>
    public sealed class InteropNameClashTests
    {
        private static readonly string[] AvaloniaNamespaces =
            ["Avalonia.Controls", "Avalonia.Input", "Avalonia.Media", "Avalonia.Platform"];

        [Fact]
        public void No_public_interop_type_shares_a_name_with_a_common_avalonia_type()
        {
            var interop = typeof(Prisc.Interop.InteropLibrary).Assembly
                .GetExportedTypes()
                .Where(t => !t.IsNested)
                .Select(t => t.Name)
                .ToHashSet(StringComparer.Ordinal);

            var avalonia = new[]
                {
                    typeof(Avalonia.Controls.Control).Assembly,
                    typeof(Avalonia.Input.Key).Assembly,
                    typeof(Avalonia.Media.Color).Assembly,
                }
                .Distinct()
                .SelectMany(a => a.GetExportedTypes())
                .Where(t => !t.IsNested && t.Namespace != null && AvaloniaNamespaces.Contains(t.Namespace))
                .Select(t => $"{t.Namespace}.{t.Name}")
                .ToList();

            Assert.NotEmpty(interop);
            Assert.NotEmpty(avalonia);
            var clashes = avalonia
                .Where(full => interop.Contains(full[(full.LastIndexOf('.') + 1)..]))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            Assert.True(clashes.Count == 0, "public Prisc.Interop types clash with Avalonia: " + String.Join(", ", clashes));
        }
    }
}
