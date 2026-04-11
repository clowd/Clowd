using System;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Ui.Models;

/// <summary>
/// Persistable model for one editor workspace. Saved as JSON in
/// <c>%APPDATA%\Clowd\sessions\{Id}.json</c>.
/// </summary>
public sealed class EditorSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public Color Background { get; set; } = Colors.Transparent;
    public double ViewportScale { get; set; } = 1.0;
    public double ViewportTx { get; set; }
    public double ViewportTy { get; set; }
    public GraphicBase[] Graphics { get; set; } = Array.Empty<GraphicBase>();
}
