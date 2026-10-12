using System.Windows;
using System.Windows.Controls;

namespace FontCrate.App.Views;

public partial class TasksPage : Page
{
    public TasksPage(MainViewModel model)
    {
        InitializeComponent(); DataContext = model;
    }
}
