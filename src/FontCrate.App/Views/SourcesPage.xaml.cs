using System.Windows.Controls;

namespace FontCrate.App.Views;

public partial class SourcesPage : Page, IDisposable
{
    private readonly SourcesManagementViewModel model;
    public SourcesPage(MainViewModel owner, ConfirmationService dialogs, Action viewFonts)
    {
        InitializeComponent(); model = new(owner, dialogs, viewFonts); DataContext = model;
        Loaded += async (_, _) => await model.RefreshAsync();
    }
    public void Dispose() => model.Dispose();
}
