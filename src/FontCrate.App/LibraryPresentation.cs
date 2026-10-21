using System.Globalization;

namespace FontCrate.App;

internal enum LibraryEmptyState { Loading, Populated, FirstUse, NoFonts, NoMatches }

internal static class LibraryPresentation
{
    public static LibraryEmptyState State(bool loading, int total, bool hasFilters, bool hasSources) =>
        loading ? LibraryEmptyState.Loading : total > 0 ? LibraryEmptyState.Populated :
        hasFilters ? LibraryEmptyState.NoMatches : hasSources ? LibraryEmptyState.NoFonts : LibraryEmptyState.FirstUse;

    public static double ParseSize(string text, double previous) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
        double.IsFinite(value) && value >= 8 && value <= 192 ? value : previous;
}

internal sealed record TaskResultSummary(string Title, string State, int? Succeeded = null, int? Failed = null,
    int? Total = null, string? Note = null, bool SourceAction = false)
{
    public string StateText => State switch
    {
        "Succeeded" => "✓ 已完成", "Failed" => "✕ 失败", "Warning" => "⚠ 需要注意",
        "Canceled" => "○ 已取消", "Running" => "正在执行", _ => "等待任务"
    };
    public string CountsText => string.Join(" · ", new[]
    {
        Succeeded is int s ? $"{s} 成功" : null,
        Failed is int f ? $"{f} 失败" : null,
        State == "Canceled" ? Total is int t && Succeeded is int a && Failed is int b
            ? $"{Math.Max(0, t - a - b)} 未完成" : "剩余任务未完成，数量未知" : null
    }.Where(x => x is not null));
}
