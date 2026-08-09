using Avalonia.Controls;
using Avalonia.Interactivity;
using TDR.Tools.Services;

namespace TDR.Tools.Views
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;

        public SettingsWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            LoadSettingsToUI();
        }

        private void LoadSettingsToUI()
        {
            ConfirmDeleteCheckBox.IsChecked = _settings.ConfirmOnDelete;
            ShowDirIndexFilesCheckBox.IsChecked = _settings.ShowDirIndexFiles;

            if (_settings.RememberPakDragAction && _settings.PakDragAction == "Extract")
                PakDragActionComboBox.SelectedIndex = 1;
            else if (_settings.RememberPakDragAction && _settings.PakDragAction == "Convert")
                PakDragActionComboBox.SelectedIndex = 2;
            else
                PakDragActionComboBox.SelectedIndex = 0;

            TrackDiscoveryModeComboBox.SelectedIndex = _settings.TrackDiscoveryMode switch
            {
                "RacesOnly"  => 1,
                "Heuristic" => 2,
                _            => 0  // "Auto" is the default
            };
            TrackDiscoveryModeComboBox.SelectionChanged += OnDiscoveryModeChanged;
            UpdateDiscoveryHint();

            ExportObjCheckBox.IsChecked = _settings.ExportObj;
            IncludeMovablePropsCheckBox.IsChecked = _settings.IncludeMovableProps;
            ExportSceneJsonCheckBox.IsChecked = _settings.ExportSceneJson;
            UseGroupingCheckBox.IsChecked = _settings.UseGrouping;
            DumpAllCheckBox.IsChecked = _settings.DumpAll;
            VerboseLogCheckBox.IsChecked = _settings.VerboseLog;
            DebugModeCheckBox.IsChecked = _settings.DebugMode;
        }

        private void OnDiscoveryModeChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
            => UpdateDiscoveryHint();

        private void UpdateDiscoveryHint()
        {
            DiscoveryModeHint.Text = TrackDiscoveryModeComboBox.SelectedIndex switch
            {
                1 => "Strict: only CARMA.pak/races.txt is consulted. If races.txt is missing, track detection stops — no guessing.",
                2 => "Heuristic: scans all .txt files for track keywords. Works without races.txt but may produce false positives.",
                _ => "Auto: reads CARMA.pak/races.txt when present; falls back to heuristic scan only if races.txt is absent."
            };
        }

        private void OnSaveClick(object? sender, RoutedEventArgs e)
        {
            _settings.ConfirmOnDelete = ConfirmDeleteCheckBox.IsChecked == true;
            _settings.ShowDirIndexFiles = ShowDirIndexFilesCheckBox.IsChecked == true;

            int actionIdx = PakDragActionComboBox.SelectedIndex;
            if (actionIdx == 1)
            {
                _settings.PakDragAction = "Extract";
                _settings.RememberPakDragAction = true;
            }
            else if (actionIdx == 2)
            {
                _settings.PakDragAction = "Convert";
                _settings.RememberPakDragAction = true;
            }
            else
            {
                _settings.PakDragAction = "Ask";
                _settings.RememberPakDragAction = false;
            }

            _settings.ExportObj = ExportObjCheckBox.IsChecked == true;
            _settings.IncludeMovableProps = IncludeMovablePropsCheckBox.IsChecked == true;
            _settings.ExportSceneJson = ExportSceneJsonCheckBox.IsChecked == true;
            _settings.UseGrouping = UseGroupingCheckBox.IsChecked == true;
            _settings.DumpAll = DumpAllCheckBox.IsChecked == true;
            _settings.VerboseLog = VerboseLogCheckBox.IsChecked == true;
            _settings.DebugMode = DebugModeCheckBox.IsChecked == true;

            _settings.TrackDiscoveryMode = TrackDiscoveryModeComboBox.SelectedIndex switch
            {
                1 => "RacesOnly",
                2 => "Heuristic",
                _ => "Auto"
            };

            _settings.Save();
            Close(true);
        }

        private void OnResetClick(object? sender, RoutedEventArgs e)
        {
            _settings = new AppSettings();
            LoadSettingsToUI();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }
    }
}
