using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Avalonia.Reactive;

internal class WeakObservable<T> : IObservable<T>
{
    private readonly IObservable<T> _source;

    #region Ctor

    /// <summary>
    /// Initializes a new instance of the <see cref="WeakObservable{T}"/> class.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <exception cref="System.ArgumentNullException">source</exception>
    public WeakObservable(IObservable<T> source)
    {
        #region Validation

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        #endregion Validation

        _source = source;
    }

    #endregion Ctor

    #region Subscribe

    /// <summary>
    /// Subscribes the specified observer.
    /// </summary>
    /// <param name="observer">The observer.</param>
    /// <returns></returns>
    public IDisposable Subscribe(IObserver<T> observer)
    {
        IObservable<T> source = _source;
        if (source == null)
            return Disposable.Empty;
        var weakObserver = new WeakObserver<T>(observer);
        IDisposable disp = source.Subscribe(weakObserver);
        return disp;
    }

    #endregion Subscribe
}

internal class WeakObserver<T> : IObserver<T>
{
    private readonly WeakReference<IObserver<T>> _target;

    #region Ctor

    /// <summary>
    /// Initializes a new instance of the <see cref="WeakObserver{T}"/> class.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <exception cref="System.ArgumentNullException">target</exception>
    public WeakObserver(IObserver<T> target)
    {
        #region Validation

        if (target == null)
            throw new ArgumentNullException(nameof(target));

        #endregion Validation

        _target = new WeakReference<IObserver<T>>(target);
    }

    #endregion Ctor

    #region Target

    /// <summary>
    /// Gets the target.
    /// </summary>
    /// <value>
    /// The target.
    /// </value>
    private IObserver<T> Target
    {
        get
        {
            IObserver<T> target;
            if (_target.TryGetTarget(out target))
                return target;
            return null;
        }
    }

    #endregion Target

    #region IObserver<T> Members

    #region OnCompleted

    /// <summary>
    /// Notifies the observer that the provider has finished sending push-based notifications.
    /// </summary>
    public void OnCompleted()
    {
        IObserver<T> target = Target;
        if (target == null)
            return;

        target.OnCompleted();
    }

    #endregion OnCompleted

    #region OnError

    /// <summary>
    /// Notifies the observer that the provider has experienced an error condition.
    /// </summary>
    /// <param name="error">An object that provides additional information about the error.</param>
    public void OnError(Exception error)
    {
        IObserver<T> target = Target;
        if (target == null)
            return;

        target.OnError(error);
    }

    #endregion OnError

    #region OnNext

    /// <summary>
    /// Provides the observer with new data.
    /// </summary>
    /// <param name="value">The current notification information.</param>
    public void OnNext(T value)
    {
        IObserver<T> target = Target;
        if (target == null)
            return;

        target.OnNext(value);
    }

    #endregion OnNext

    #endregion IObserver<T> Members
}

public static class WeakObservableExtensions
{
    #region ToWeakObservable

    /// <summary>
    /// To weak observable.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="source">The source.</param>
    /// <returns></returns>
    public static IObservable<T> ToWeakObservable<T>(this IObservable<T> source)
    {
        var result = new WeakObservable<T>(source);
        return result;
    }

    #endregion ToWeakObservable
}
