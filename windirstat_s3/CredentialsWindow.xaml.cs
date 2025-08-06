using System.Windows;
using Amazon;

namespace windirstat_s3;

public partial class CredentialsWindow : Window
{
    public CredentialsWindow()
    {
        InitializeComponent();
        RegionComboBox.ItemsSource = RegionEndpoint.EnumerableAllRegions;
        RegionComboBox.SelectedItem = RegionEndpoint.USEast1;
    }

    public string AccessKeyId => AccessKeyTextBox.Text;
    public string SecretAccessKey => SecretKeyBox.Password;
    public RegionEndpoint? SelectedRegion => RegionComboBox.SelectedItem as RegionEndpoint;
    public bool SaveCredentials => SaveCheckBox.IsChecked == true;

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AccessKeyId) || string.IsNullOrWhiteSpace(SecretAccessKey) || SelectedRegion == null)
        {
            MessageBox.Show("Informe a chave, segredo e região.");
            return;
        }
        DialogResult = true;
    }
}
