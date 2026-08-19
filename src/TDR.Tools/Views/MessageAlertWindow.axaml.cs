using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace TDR.Tools.Views
{
    public partial class MessageAlertWindow : Window
    {
        public MessageAlertWindow()
        {
            InitializeComponent();
        }

        public MessageAlertWindow(string title, string message) : this()
        {
            Title = title;
            TitleTextBlock.Text = title;
            MessageTextBlock.Text = message;
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        public static async Task ShowAsync(Window owner, string title, string message)
        {
            var dialog = new MessageAlertWindow(title, message);
            await dialog.ShowDialog(owner);
        }
    }
}
