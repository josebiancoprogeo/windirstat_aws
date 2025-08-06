using System;
using System.Windows;
using Amazon;
using Amazon.S3;
using windirstat_s3.Services;

namespace windirstat_s3;

public partial class MainWindow : Window
{
    private readonly AwsProfileManager _profileManager = new();

    public MainWindow()
    {
        InitializeComponent();
        ProfileComboBox.ItemsSource = _profileManager.ListProfiles();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var profileName = ProfileComboBox.SelectedItem as string;
        var bucketName = BucketTextBox.Text;

        if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(bucketName))
        {
            MessageBox.Show("Selecione um perfil e informe o nome do bucket.");
            return;
        }

        try
        {
            var credentials = _profileManager.GetCredentials(profileName);
            using var client = new AmazonS3Client(credentials, RegionEndpoint.USEast1);
            var scanner = new S3Scanner(client);
            var result = await scanner.ScanAsync(bucketName);
            MessageBox.Show($"Total bytes: {result.Size}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
