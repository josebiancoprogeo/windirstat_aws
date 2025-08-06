using System;
using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
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
