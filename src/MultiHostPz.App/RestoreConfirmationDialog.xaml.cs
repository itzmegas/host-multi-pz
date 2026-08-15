using System.Windows;
using MultiHostPz.App.Localization;

namespace MultiHostPz.App;

public partial class RestoreConfirmationDialog : Window
{
    public RestoreConfirmationDialog(LocalizedText text)
    {
        InitializeComponent();
        Title = text["ConfirmRestoreTitle"];
        MessageText.Text = text["ConfirmRestoreMessage"];
        YesButton.Content = text["Yes"];
        NoButton.Content = text["No"];
    }

    private void YesButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void NoButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
