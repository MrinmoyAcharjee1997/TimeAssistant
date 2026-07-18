using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TimeTracker.App.ViewModels;

namespace TimeTracker.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; } = new();

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ViewModel.InitializeAsync();
        if (!ViewModel.HasLogIssues) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Time log needs repair",
            Content = ViewModel.LogIssueMessage,
            PrimaryButtonText = "Repair and back up",
            CloseButtonText = "Keep original"
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RepairLogCommand.ExecuteAsync(null);
        }
    }
}
