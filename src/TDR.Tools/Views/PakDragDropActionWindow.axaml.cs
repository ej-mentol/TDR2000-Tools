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
        public bool UnpackInnerPaks { get; private set; } = true;
        public PakUserAction SelectedAction { get; private set; } = PakUserAction.Cancel;

        public PakDragDropActionWindow()
        {
            InitializeComponent();
        }

        public PakDragDropActionWindow(string itemName, bool isFolder = false, bool containsPakFiles = true) : this()
        {
            TargetPakTextBlock.Text = isFolder
                ? $"Selected folder: '{itemName}'"
                : $"Selected archive: '{itemName}'";

            FileOpsText.Text = isFolder ? "File Ops" : "Unpack Ops";
            UnpackInnerPaks = containsPakFiles;
        }

        private void OnCopyAndUnpackClick(object? sender, RoutedEventArgs e)
        {
            UnpackInnerPaks = true;
            SelectedAction = PakUserAction.Extract;
            Close(PakUserAction.Extract);
        }

        private void OnCopyOnlyClick(object? sender, RoutedEventArgs e)
        {
            UnpackInnerPaks = false;
            SelectedAction = PakUserAction.Extract;
            Close(PakUserAction.Extract);
        }

        private void OnUnpackOnlyClick(object? sender, RoutedEventArgs e)
        {
            UnpackInnerPaks = true;
            SelectedAction = PakUserAction.Extract;
            Close(PakUserAction.Extract);
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
