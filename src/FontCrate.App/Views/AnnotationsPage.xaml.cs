using System.Windows.Controls;

namespace FontCrate.App.Views;

public partial class AnnotationsPage : Page, IDisposable
{
    private readonly AnnotationsManagementViewModel model;
    public AnnotationsPage(MainViewModel owner, ConfirmationService dialogs, Action viewFonts)
    { InitializeComponent(); model = new(owner, dialogs, viewFonts); DataContext = model; }
    public void Dispose() => model.Dispose();
}
