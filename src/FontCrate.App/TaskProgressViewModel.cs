using CommunityToolkit.Mvvm.ComponentModel;

namespace FontCrate.App;

public partial class TaskProgressViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;
    [ObservableProperty] private string state = "Idle";
    [ObservableProperty] private string message = "准备就绪";
    [ObservableProperty] private string caption = "等待任务";
    [ObservableProperty] private double value;
    [ObservableProperty] private bool indeterminate;

    public void Begin(string description, bool unknownTotal = true)
    {
        State = "Running"; Message = description; Caption = unknownTotal ? "准备中…" : "0%";
        Value = 0; Indeterminate = unknownTotal;
    }
    public void Report(int completed, int total, bool knownTotal, string description, string counts)
    {
        Indeterminate = !knownTotal;
        // Reserve 100% for the final durable completion, including state reconciliation.
        Value = knownTotal ? Math.Min(99, total == 0 ? 0 : completed * 100d / total) : 0;
        Message = description;
        Caption = knownTotal ? $"{Value:F0}% · {counts}" : counts;
    }
    public void Finish(string description, string finalState = "Succeeded", bool completed = true)
    {
        bool unknownTotal = Indeterminate;
        Indeterminate = false;
        if (completed) Value = 100;
        Message = description; State = finalState;
        if (completed) Caption = "100% · " + description;
        else Caption = unknownTotal ? $"已停止 · {description}" : $"已停止 · {Value:F0}% · {description}";
    }
}

internal sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
