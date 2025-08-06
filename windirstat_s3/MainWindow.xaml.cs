using System;
using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using Amazon;
using Amazon.S3;
using windirstat_s3.Services;
using windirstat_s3.ViewModels;

namespace windirstat_s3;

public partial class MainWindow : Window
{
    private readonly AwsProfileManager _profileManager = new();
    private DirectoryNodeViewModel? _root;
    private FolderNode? _result;
    private ICollectionView? _extensionView;

    public MainWindow()
    {
        InitializeComponent();

        var profiles = _profileManager.ListProfiles().ToList();
        if (!profiles.Any())
        {
            var dialog = new CredentialsWindow { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SaveCredentials && dialog.SelectedRegion != null)
            {
                _profileManager.SaveProfile("default", dialog.AccessKeyId, dialog.SecretAccessKey, dialog.SelectedRegion);
                profiles = _profileManager.ListProfiles().ToList();
            }
        }

        ProfileComboBox.ItemsSource = profiles;
        if (profiles.Any())
        {
            ProfileComboBox.SelectedIndex = 0;
            _ = LoadBucketsForSelectedProfileAsync();
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var profileName = ProfileComboBox.SelectedItem as string;
        var bucketName = BucketComboBox.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(bucketName))
        {
            MessageBox.Show("Selecione um perfil e informe o nome do bucket.");
            return;
        }

        try
        {
            var credentials = _profileManager.GetCredentials(profileName);
            var region = _profileManager.GetRegion(profileName);
            using var client = new AmazonS3Client(credentials, region);
            var scanner = new S3Scanner(client);
            var prefixes = IgnorePrefixesTextBox.Text
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _result = await scanner.ScanAsync(bucketName, prefixes);
            _root = new DirectoryNodeViewModel(_result);
            ResultTree.ItemsSource = _root.Children;
            ResultTreemap.ItemsSource = null;
            ExtensionDataGrid.ItemsSource = null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadBucketsForSelectedProfileAsync();
    }

    private async Task LoadBucketsForSelectedProfileAsync()
    {
        var profileName = ProfileComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            BucketComboBox.ItemsSource = null;
            return;
        }

        try
        {
            var credentials = _profileManager.GetCredentials(profileName);
            var region = _profileManager.GetRegion(profileName);
            using var client = new AmazonS3Client(credentials, region);
            var response = await client.ListBucketsAsync();
            var buckets = response.Buckets.Select(b => b.BucketName).OrderBy(b => b).ToList();
            BucketComboBox.ItemsSource = buckets;
            if (buckets.Any())
            {
                BucketComboBox.SelectedIndex = 0;
            }
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
            ResultTreemap.ItemsSource = node.Children;
            _extensionView = CollectionViewSource.GetDefaultView(node.Extensions);
            _extensionView.SortDescriptions.Clear();
            _extensionView.SortDescriptions.Add(new SortDescription(nameof(ExtensionInfoViewModel.Size), ListSortDirection.Descending));
            ExtensionDataGrid.ItemsSource = _extensionView;
            ApplyExtensionFilter();
        }
    }

    private void ExtensionFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyExtensionFilter();
    }

    private void ApplyExtensionFilter()
    {
        if (_extensionView == null)
        {
            return;
        }

        var filterText = ExtensionFilterTextBox.Text?.ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(filterText))
        {
            _extensionView.Filter = null;
        }
        else
        {
            _extensionView.Filter = item => item is ExtensionInfoViewModel ext && ext.Extension.ToLowerInvariant().Contains(filterText);
        }
        _extensionView.Refresh();
    }

    private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null)
        {
            MessageBox.Show("Nenhum resultado para exportar.");
            return;
        }

        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog() == true)
        {
            ReportExporter.ToCsv(_result, dialog.FileName);
        }
    }

    private void ExportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null)
        {
            MessageBox.Show("Nenhum resultado para exportar.");
            return;
        }

        var dialog = new SaveFileDialog { Filter = "JSON files (*.json)|*.json" };
        if (dialog.ShowDialog() == true)
        {
            ReportExporter.ToJson(_result, dialog.FileName);
        }
    }
}
