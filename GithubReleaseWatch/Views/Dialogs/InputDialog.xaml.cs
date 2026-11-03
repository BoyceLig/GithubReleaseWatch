using System.Windows;

namespace GithubReleaseWatch
{
    public partial class InputDialog : Window
    {
        public string Value { get; private set; } = "";

        public InputDialog(string title, string prompt, string initial = "")
        {
            InitializeComponent();
            Title = title;
            LblPrompt.Text = prompt;
            TxtValue.Text = initial;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Value = TxtValue.Text.Trim();
            DialogResult = true;
        }
    }
}
