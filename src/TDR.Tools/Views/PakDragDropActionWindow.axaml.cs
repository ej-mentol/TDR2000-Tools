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
        public bool CreateSubfolder { get; private set; } = true;
        public bool FlatFiles { get; private set; } = false;
        public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;
        public PakUserAction SelectedAction { get; private set; } = PakUserAction.Cancel;

        public bool ContainsPakFiles { get; private set; } = true;

        public PakDragDropActionWindow()
        {
            InitializeComponent();
        }

        public PakDragDropActionWindow(string itemName, bool isFolder = false, bool containsPakFiles = true) : this()
        {
            ContainsPakFiles = containsPakFiles;
            TargetPakTextBlock.Text = isFolder
                ? $"Selected folder: '{itemName}'"
                : $"Selected archive: '{itemName}'";

            FileOpsText.Text = isFolder ? "File Ops" : "Unpack Ops";
        }

        private void OnCopyAndUnpackClick(object? sender, RoutedEventArgs e)
        {
            CreateSubfolder = true;
            FlatFiles = false;
            SelectedAction = PakUserAction.Extract;
            Close(PakUserAction.Extract);
        }

        private void OnCopyOnlyClick(object? sender, RoutedEventArgs e)
        {
            CreateSubfolder = false;
            FlatFiles = true;
            SelectedAction = PakUserAction.Extract;
            Close(PakUserAction.Extract);
        }

        private void OnUnpackOnlyClick(object? sender, RoutedEventArgs e)
        {
            OnCopyAndUnpackClick(sender, e);
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
