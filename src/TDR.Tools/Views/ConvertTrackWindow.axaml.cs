using Avalonia.Controls;
using Avalonia.Interactivity;
using TDR.Tools.ViewModels;

namespace TDR.Tools.Views
{
    public partial class ConvertTrackWindow : Window
    {
        public ConvertTrackWindow()
        {
            InitializeComponent();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is ConvertTrackModalViewModel vm)
            {
                vm.Cancel();
            }
            Close();
        }

        private void OnConfirmClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is ConvertTrackModalViewModel vm)
            {
                vm.ConfirmExport();
            }
            Close();
        }

        private void OnSelectAllClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is ConvertTrackModalViewModel vm)
            {
                vm.SetAllHieNodesSelected(vm.HieTreeNodes, true);
                vm.NotifyUserTreeToggled();
            }
        }

        private void OnDeselectAllClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is ConvertTrackModalViewModel vm)
            {
                vm.SetAllHieNodesSelected(vm.HieTreeNodes, false);
                vm.NotifyUserTreeToggled();
            }
        }
    }
}
