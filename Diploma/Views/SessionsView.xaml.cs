using System.Windows.Controls;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class SessionsView : UserControl
{
    public SessionsView(SessionsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true)
                await viewModel.LoadSessionsCommand.ExecuteAsync(null);
        };
    }
}