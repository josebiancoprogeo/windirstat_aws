using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Amazon;
using Amazon.S3;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using windirstat_s3.Services;
using windirstat_s3.ViewModels;

namespace windirstat_s3;

public partial class MainWindow : Window
{
    private readonly AwsProfileManager _profileManager = new();
    private DirectoryNodeViewModel? _root;

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
            _root = new DirectoryNodeViewModel(result);
            ResultTree.ItemsSource = _root.Children;
            UpdateChart(Array.Empty<DirectoryNodeViewModel>());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResultTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ResultTree.SelectedItem is DirectoryNodeViewModel node)
        {
            UpdateChart(node.Children);
        }
    }

    private void UpdateChart(IEnumerable<DirectoryNodeViewModel> nodes)
    {
        ResultChart.Series = nodes
            .Select(n => new PieSeries<double>
            {
                Values = new[] { (double)n.Size },
                Name = n.Name
            })
            .Cast<ISeries>()
            .ToArray();
    }
}
