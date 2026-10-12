using System.Windows.Controls;

namespace FontCrate.App.Views;

public partial class CategoryManagementControl : UserControl
{
    public CategoryManagementControl()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is CategoryManagementViewModel model) await model.RefreshAsync(); };
    }
}
