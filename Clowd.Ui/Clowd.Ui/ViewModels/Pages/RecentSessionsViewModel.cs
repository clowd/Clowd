using System.Collections.ObjectModel;
using Clowd.Ui.Models;
using Clowd.Ui.Models.Common;
using Clowd.Ui.Services;

namespace Clowd.Ui.ViewModels.Pages;

public sealed class RecentSessionsViewModel : ObservableObject
{
    private readonly SessionStore _store;
    private readonly IEditorWindowService _editor;

    public ObservableCollection<EditorSession> Sessions { get; } = new();

    public bool HasSessions => Sessions.Count > 0;

    public RecentSessionsViewModel(SessionStore store, IEditorWindowService editor)
    {
        _store = store;
        _editor = editor;
        Refresh();
    }

    public void Refresh()
    {
        Sessions.Clear();
        foreach (var s in _store.ListSessions())
            Sessions.Add(s);
        OnPropertyChanged(nameof(HasSessions));
    }

    public void NewSession()
    {
        _editor.OpenNew();
    }

    public void OpenSession(EditorSession session)
    {
        _editor.Open(session);
    }
}
