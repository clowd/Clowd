using Clowd.Ui.Models;
using Clowd.Ui.Views;

namespace Clowd.Ui.Services;

public sealed class EditorWindowService : IEditorWindowService
{
    private readonly SessionStore _sessions;

    public EditorWindowService(SessionStore sessions)
    {
        _sessions = sessions;
    }

    public void OpenNew()
    {
        var session = new EditorSession { Name = "Untitled" };
        Open(session);
    }

    public void Open(EditorSession session)
    {
        var window = new EditorWindow(session, _sessions);
        window.Show();
    }
}
