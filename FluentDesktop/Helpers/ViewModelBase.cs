using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace FluentDesktop.Helpers;

/// <summary>
/// Base for view models. Created on the UI thread; <see cref="OnUI"/> marshals
/// callbacks raised by the backend on worker threads.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    protected DispatcherQueue Dispatcher { get; } = DispatcherQueue.GetForCurrentThread();

    protected void OnUI(Action action)
    {
        if (Dispatcher.HasThreadAccess) action();
        else Dispatcher.TryEnqueue(() => action());
    }
}
