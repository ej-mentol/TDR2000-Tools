using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace TDR.Tools.Views
{
    public partial class ConfirmDeleteWindow : Window
    {
        private const string TrashIconData = "M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16";
        private const string PermanentCrossIconData = "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z";

        public bool IsConfirmed { get; private set; }
        public bool DontAskAgain => DontAskCheckBox.IsChecked == true;

        public ConfirmDeleteWindow()
        {
            InitializeComponent();
        }

        public ConfirmDeleteWindow(string itemName, bool isPermanent = false) : this()
        {
            if (isPermanent)
            {
                Title = "Permanent Delete";
                HeaderTextBlock.Text = "Permanently Delete Item(s)?";
                TargetItemTextBlock.Text = $"Are you sure you want to permanently delete '{itemName}'?";
                WarningTextBlock.IsVisible = true;
                DeleteIcon.Data = StreamGeometry.Parse(PermanentCrossIconData);
                DeleteIcon.Foreground = new SolidColorBrush(Color.Parse("#FF4D4D"));
                DeleteButton.Content = "Delete Permanently";
                DeleteButton.Background = new SolidColorBrush(Color.Parse("#D32F2F"));
                DontAskCheckBox.IsVisible = false;
            }
            else
            {
                Title = "Confirm Delete";
                HeaderTextBlock.Text = "Move to Recycle Bin";
                TargetItemTextBlock.Text = $"Are you sure you want to send '{itemName}' to the Recycle Bin?";
                WarningTextBlock.IsVisible = false;
                DeleteIcon.Data = StreamGeometry.Parse(TrashIconData);
                DeleteIcon.Foreground = new SolidColorBrush(Color.Parse("#E55353"));
                DeleteButton.Content = "Delete";
                DeleteButton.Background = new SolidColorBrush(Color.Parse("#C53939"));
                DontAskCheckBox.IsVisible = true;
            }
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
