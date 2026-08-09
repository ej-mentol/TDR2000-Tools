using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TDR.Tools.Views
{
    public partial class ConfirmDeleteWindow : Window
    {
        public bool IsConfirmed { get; private set; }
        public bool DontAskAgain => DontAskCheckBox.IsChecked == true;

        public ConfirmDeleteWindow()
        {
            InitializeComponent();
        }

        public ConfirmDeleteWindow(string itemName) : this()
        {
            TargetItemTextBlock.Text = $"Are you sure you want to delete '{itemName}'?";
        }

        private void OnDeleteClick(object? sender, RoutedEventArgs e)
        {
            IsConfirmed = true;
            Close(true);
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            Close(false);
        }
    }
}
