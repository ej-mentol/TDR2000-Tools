using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TDR.Tools.Views
{
    public enum PakUserAction
    {
        Cancel,
        Extract,
        Convert
    }

    public partial class PakDragDropActionWindow : Window
    {
        public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;
        public PakUserAction SelectedAction { get; private set; } = PakUserAction.Cancel;

        public PakDragDropActionWindow()
        {
            InitializeComponent();
        }

        public PakDragDropActionWindow(string pakName) : this()
        {
            TargetPakTextBlock.Text = $"Selected archive: '{pakName}'";
        }

        private void OnExtractClick(object? sender, RoutedEventArgs e)
        {
            SelectedAction = PakUserAction.Extract;
            Close(PakUserAction.Extract);
        }

        private void OnConvertClick(object? sender, RoutedEventArgs e)
        {
            SelectedAction = PakUserAction.Convert;
            Close(PakUserAction.Convert);
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            SelectedAction = PakUserAction.Cancel;
            Close(PakUserAction.Cancel);
        }
    }
}
