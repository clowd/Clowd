using Clowd.Ui.Models;

namespace Clowd.Ui.Services;

/// <summary>
/// Abstraction over the editor window factory so view-models can request a
/// new (or existing) editor session without taking a hard dependency on the
/// concrete <c>EditorWindow</c> view.
/// </summary>
public interface IEditorWindowService
{
    void OpenNew();
    void Open(EditorSession session);
}
