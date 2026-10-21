using CommunityToolkit.Mvvm.Input;

namespace FontCrate.App;

public partial class MainViewModel
{
    private bool libraryLoading = true;
    public bool LibraryLoading
    {
        get => libraryLoading;
        private set { SetProperty(ref libraryLoading, value); NotifyLibraryState(); }
    }
    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || Filter(SelectedSource) is not null ||
        Filter(TagPanel.SelectedFilter) is not null || FavoritesOnly || State != "全部";
    public bool HasSelection => SelectionCount > 0;
    private LibraryEmptyState EmptyState => LibraryPresentation.State(LibraryLoading, Total, HasFilters, SourceItems.Any(s => s.Id != 0));
    public bool ShowLibraryMessage => EmptyState != LibraryEmptyState.Populated;
    public bool ShowLibraryAction => EmptyState is not (LibraryEmptyState.Loading or LibraryEmptyState.Populated);
    public string LibraryMessage => EmptyState switch
    {
        LibraryEmptyState.Loading => "正在加载字体库…", LibraryEmptyState.FirstUse => "添加字体目录，开始浏览和预览字体。",
        LibraryEmptyState.NoFonts => "来源中尚无可浏览的字体，请检查目录或重新扫描。",
        LibraryEmptyState.NoMatches => "没有匹配的字体，试试调整搜索或筛选条件。", _ => ""
    };
    public string LibraryActionLabel => EmptyState switch
    {
        LibraryEmptyState.NoMatches => "清除筛选", LibraryEmptyState.NoFonts => "管理来源", _ => "添加目录"
    };
    public event Action? SourcesRequested;
    private void NotifyLibraryState()
    {
        OnPropertyChanged(nameof(HasFilters)); OnPropertyChanged(nameof(ShowLibraryMessage));
        OnPropertyChanged(nameof(ShowLibraryAction)); OnPropertyChanged(nameof(LibraryMessage)); OnPropertyChanged(nameof(LibraryActionLabel));
    }
    [RelayCommand] private async Task LibraryEmptyActionAsync()
    {
        if (EmptyState == LibraryEmptyState.NoMatches) ClearFilters();
        else if (EmptyState == LibraryEmptyState.NoFonts) SourcesRequested?.Invoke();
        else if (EmptyState == LibraryEmptyState.FirstUse) await AddDirectoryAsync();
    }
    [RelayCommand] private void PreviewPreset(string key) => PreviewText = key switch
    {
        "Chinese" => "字体之美，始于选择。\n天地玄黄，宇宙洪荒。",
        "English" => "The quick brown fox jumps over the lazy dog.\nABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz",
        "Numbers" => "0123456789\n+ − × ÷ = % @ # & ( )", "List" => ListPreviewText, _ => PreviewText
    };
    public void CommitPreviewSize(string text) => FontSize = LibraryPresentation.ParseSize(text, FontSize);

    private TaskResultSummary resultSummary = new("最近任务", "Idle");
    internal TaskResultSummary ResultSummary
    {
        get => resultSummary;
        private set
        {
            resultSummary = value;
            OnPropertyChanged(nameof(ResultSummary)); OnPropertyChanged(nameof(ResultTitle));
            OnPropertyChanged(nameof(ResultState)); OnPropertyChanged(nameof(ResultCounts));
            OnPropertyChanged(nameof(ResultNote)); OnPropertyChanged(nameof(HasResultAction));
            OnPropertyChanged(nameof(ResultActionLabel));
        }
    }
    public string ResultTitle => ResultSummary.Title;
    public string ResultState => ResultSummary.StateText;
    public string ResultCounts => ResultSummary.CountsText;
    public string ResultNote => ResultSummary.Note ?? "";
    public bool HasResultAction => ResultSummary.State is "Warning" or "Failed" or "Canceled";
    public string ResultActionLabel => ResultSummary.SourceAction ? "检查来源" : "核对与恢复状态";
    [RelayCommand] private async Task ResultActionAsync()
    {
        if (!CanManage) return;
        if (ResultSummary.SourceAction) SourcesRequested?.Invoke();
        else await RecoverAsync();
    }
    private Exception? presentedError;
    private void RecordTaskFailure(string title, Exception error)
    {
        presentedError = error;
        Status = error.Message; ResultText = error.ToString();
        ResultSummary = new(title, "Failed", Note: error.Message, SourceAction: Scanning);
    }
    private void Summarize(string title, IEnumerable<bool> outcomes, bool canceled = false, int? total = null, bool sourceAction = false)
    {
        var values = outcomes.ToArray();
        ResultSummary = new(title, canceled ? "Canceled" : values.Any(x => !x) ? "Warning" : "Succeeded",
            values.Count(x => x), values.Count(x => !x), total, SourceAction: sourceAction);
    }
}
