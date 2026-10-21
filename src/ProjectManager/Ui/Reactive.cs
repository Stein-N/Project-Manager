// A small reactive layer like the signals of the Rust UI: a Var holds a value and tells its watchers when it changes;
// Watch keeps a control up to date while it is on screen.

using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ProjectManager.Ui;

public interface IVar
{
    event Action? Changed;
}

/// <summary>
/// An observable value (a Leptos RwSignal); also an <see cref="ObservableObject"/>, so XAML can bind to
/// <see cref="Value"/>. Set it on the UI thread.
/// </summary>
public sealed class Var<T>(T initial) : ObservableObject, IVar
{
    private T _value = initial;

    public event Action? Changed;

    public T Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value)) Changed?.Invoke();
        }
    }

    /// <summary>Changes a mutable value in place and tells the watchers.</summary>
    public void Update(Action<T> change)
    {
        change(_value);
        Notify();
    }

    /// <summary>Tells the watchers although the value did not change (e.g. a list was changed in place).</summary>
    public void Notify()
    {
        OnPropertyChanged(nameof(Value));
        Changed?.Invoke();
    }

    public override string ToString() => _value?.ToString() ?? "";
}

/// <summary>A value computed from other vars (a Leptos Signal::derive); recomputed when one of them changes.</summary>
public sealed class Derived<T> : IVar
{
    private readonly Func<T> _compute;

    public Derived(Func<T> compute, params IVar[] deps)
    {
        _compute = compute;
        foreach (var d in deps) d.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public T Value => _compute();
}

public static class React
{
    /// <summary>
    /// Runs <paramref name="update"/> now and whenever one of <paramref name="deps"/> changes, as long as
    /// <paramref name="owner"/> is on screen (watchers are removed when it leaves, so views do not leak).
    /// </summary>
    public static TControl Watch<TControl>(this TControl owner, Action update, params IVar[] deps) where TControl : Control
    {
        var subscribed = false;
        void OnChanged()
        {
            if (Dispatcher.UIThread.CheckAccess()) update();
            else Dispatcher.UIThread.Post(update);
        }
        void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            foreach (var d in deps) d.Changed += OnChanged;
        }
        void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            foreach (var d in deps) d.Changed -= OnChanged;
        }
        update();
        Subscribe();
        owner.AttachedToVisualTree += (_, _) =>
        {
            if (subscribed) return;
            Subscribe();
            update();
        };
        owner.DetachedFromVisualTree += (_, _) => Unsubscribe();
        return owner;
    }

    /// <summary>Runs <paramref name="effect"/> whenever one of <paramref name="deps"/> changes while <paramref name="owner"/> is on screen (not now).</summary>
    public static TControl OnChange<TControl>(this TControl owner, Action effect, params IVar[] deps) where TControl : Control
    {
        var first = true;
        return owner.Watch(() =>
        {
            if (first)
            {
                first = false;
                return;
            }
            effect();
        }, deps);
    }

    /// <summary>Starts async work on the UI thread without waiting for it; errors are swallowed like a Leptos spawn_local.</summary>
    public static async void Spawn(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
        }
    }
}
