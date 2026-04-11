using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace Clowd.Ui.Services;

public enum PageKey
{
    Recent,
    General,
    Hotkeys,
    Editor,
    Uploads,
    About,
}

/// <summary>
/// Resolves a PageKey to a Control instance. The MainWindow asks for views via this service
/// rather than newing them up itself, so view registration stays in one place.
/// </summary>
public sealed class NavigationService
{
    private readonly Dictionary<PageKey, Func<Control>> _factories = new();

    public void Register(PageKey key, Func<Control> factory)
    {
        _factories[key] = factory;
    }

    public Control Resolve(PageKey key)
    {
        if (!_factories.TryGetValue(key, out var factory))
            throw new InvalidOperationException($"No view registered for {key}");
        return factory();
    }
}
