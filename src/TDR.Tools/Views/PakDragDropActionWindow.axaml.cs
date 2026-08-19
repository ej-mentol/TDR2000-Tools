using Avalonia;
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
        public bool UnpackOnly { get; private set; } = false;
        public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;
        public PakUserAction SelectedAction { get; private set; } = PakUserAction.Cancel;

        public bool ContainsPakFiles { get; private set; } = true;

        public PakDragDropActionWindow()
        {
            InitializeComponent();
        }

        public PakDragDropActionWindow(string itemName, bool isFolder = false, bool containsPakFiles = true, bool isTrack = true) : this()
        {
            ContainsPakFiles = containsPakFiles;
            TargetPakTextBlock.Text = isFolder
                ? $"Selected folder: '{itemName}'"
                : $"Selected archive: '{itemName}'";

            FileOpsText.Text = isFolder ? "File Ops" : "Unpack Ops";

            if (!isTrack)
            {
                ConvertButton.IsEnabled = false;
                ConvertButton.IsDefault = false;
                ToolTip.SetTip(ConvertButton, "Selected archive/folder does not contain a valid 3D Track descriptor.");

                if (Application.Current != null && Application.Current.TryGetResource("AccentBrush", null, out object? accentObj) && accentObj is Avalonia.Media.IBrush brush)
                {
                    FileOpsButton.Background = brush;
                    FileOpsButton.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E0E0E"));
                }
            }
        }

        protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Avalonia.Input.Key.Enter && !ConvertButton.IsEnabled)
            {
                OnCopyAndUnpackClick(this, e);
                e.Handled = true;
            }
        }

        private void OnCopyAndUnpackClick(object? sender, RoutedEventArgs e)
        {
            CreateSubfolder = true;
            FlatFiles = false;
            UnpackOnly = false;
            SelectedAction = PakUserAction.Extract;
            Close(PakUserAction.Extract);
        }

        private void OnCopyOnlyClick(object? sender, RoutedEventArgs e)
        {
            CreateSubfolder = false;
            FlatFiles = true;
            UnpackOnly = false;
            SelectedAction = PakUserAction.Extract;
            Close(PakUserAction.Extract);
        }

        private void OnUnpackOnlyClick(object? sender, RoutedEventArgs e)
        {
            CreateSubfolder = true;
            FlatFiles = false;
            UnpackOnly = true;
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
