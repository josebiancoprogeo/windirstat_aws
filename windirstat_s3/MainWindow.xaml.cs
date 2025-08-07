using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Amazon.S3;
using Microsoft.Win32;
using windirstat_s3.Services;
using windirstat_s3.ViewModels;
using Windirstat.Core;
using Windirstat.Core.Models;

namespace windirstat_s3;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AwsProfileManager _profileManager = new();
    private FolderNode? _result;
    private CancellationTokenSource? _cts;
    private Stopwatch? _stopwatch;

    public ObservableCollection<DirectoryNodeViewModel> RootNodes { get; } = new();
    private ObservableCollection<DirectoryNodeViewModel> _selectedNodeChildren = new();
    public ObservableCollection<DirectoryNodeViewModel> SelectedNodeChildren
    {
        get => _selectedNodeChildren;
        set { _selectedNodeChildren = value; OnPropertyChanged(nameof(SelectedNodeChildren)); }
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnPropertyChanged(nameof(ProgressPercent)); }
    }

    private string _scanTime = string.Empty;
    public string ScanTime
    {
        get => _scanTime;
        set { _scanTime = value; OnPropertyChanged(nameof(ScanTime)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

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

        _cts = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        ProgressPercent = 0;
        _stopwatch = Stopwatch.StartNew();
        var progress = new Progress<double>(p => ProgressPercent = p);

        try
        {
            var credentials = _profileManager.GetCredentials(profileName);
            var region = _profileManager.GetRegion(profileName);
            using var client = new AmazonS3Client(credentials, region);
            var scanner = new S3Scanner(client);
            var prefixes = IgnorePrefixesTextBox.Text
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _result = await scanner.ScanAsync(bucketName, prefixes, progress, _cts.Token);
            var rootView = new DirectoryNodeViewModel(_result, _result.Size);
            RootNodes.Clear();
            RootNodes.Add(rootView);
            SelectedNodeChildren = new ObservableCollection<DirectoryNodeViewModel>(rootView.Children);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Varredura cancelada.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CancelButton.IsEnabled = false;
            _cts = null;
            ProgressPercent = 0;
            if (_stopwatch != null)
            {
                _stopwatch.Stop();
                ScanTime = _stopwatch.Elapsed.ToString("mm\\:ss");
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
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
            SelectedNodeChildren = new ObservableCollection<DirectoryNodeViewModel>(node.Children);
        }
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
