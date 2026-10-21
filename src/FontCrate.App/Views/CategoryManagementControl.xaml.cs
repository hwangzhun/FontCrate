using System.Windows.Controls;
using System.Windows;

namespace FontCrate.App.Views;

public partial class CategoryManagementControl : UserControl
{
    public CategoryManagementControl()
    {
        InitializeComponent();
        Workspace.SizeChanged += (_, e) => { if (e.WidthChanged) UpdatePanes(); };
        Loaded += (_, _) => UpdatePanes();
        Loaded += async (_, _) => { if (DataContext is CategoryManagementViewModel model) await model.RefreshAsync(); };
    }
    private bool? compact;
    private bool detailVisible;
    private void ShowList(object sender, RoutedEventArgs e) { detailVisible = false; UpdatePanes(force: true); }
    private void ShowDetail(object sender, RoutedEventArgs e) { detailVisible = true; UpdatePanes(force: true); }
    private void UpdatePanes(bool force = false)
    {
        bool narrow = Workspace.ActualWidth < 800;
        if (!force && compact == narrow) return;
        if (compact != narrow) { compact = narrow; detailVisible = false; }
        PaneSwitch.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        ListCard.Visibility = narrow && detailVisible ? Visibility.Collapsed : Visibility.Visible;
        DetailCard.Visibility = narrow && !detailVisible ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(DetailCard, narrow ? 0 : 2);
        Workspace.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(280);
        Workspace.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 16);
        Workspace.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }
}
