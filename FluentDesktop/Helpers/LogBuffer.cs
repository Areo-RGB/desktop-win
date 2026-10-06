using System.Collections.ObjectModel;

namespace FluentDesktop.Helpers;

/// <summary>Bounded, UI-thread-only list of console lines.</summary>
public sealed class LogBuffer(int capacity = 500) : ObservableCollection<string>
{
    public int Capacity { get; } = capacity;

    public void Append(string line)
    {
        Add(line);
        while (Count > Capacity) RemoveAt(0);
    }

    public void ReplaceAll(IEnumerable<string> lines)
    {
        ClearItems();
        foreach (var line in lines.TakeLast(Capacity)) Add(line);
    }

    public string Text => string.Join(Environment.NewLine, this);
}
