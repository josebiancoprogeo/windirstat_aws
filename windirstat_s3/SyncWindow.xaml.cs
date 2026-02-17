using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Amazon.S3;
using Amazon.S3.Model;
using windirstat_s3.Services;
using WinForms = System.Windows.Forms;

namespace windirstat_s3;

public partial class SyncWindow : Window, INotifyPropertyChanged
{
    private readonly AwsProfileManager _profileManager = new();
    private double _progressPercent;
    private string _statusMessage = string.Empty;
    private int _totalFiles;
    private int _downloadedFiles;
    private int _skippedFiles;
    private int _remainingFiles;
    private string _speedText = "0 B/s";
    private string _etaText = "-";

    public double ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnPropertyChanged(nameof(ProgressPercent)); }
    }

    public int TotalFiles
    {
        get => _totalFiles;
        set { _totalFiles = value; OnPropertyChanged(nameof(TotalFiles)); }
    }

    public int DownloadedFiles
    {
        get => _downloadedFiles;
        set { _downloadedFiles = value; OnPropertyChanged(nameof(DownloadedFiles)); }
    }

    public int SkippedFiles
    {
        get => _skippedFiles;
        set { _skippedFiles = value; OnPropertyChanged(nameof(SkippedFiles)); }
    }

    public int RemainingFiles
    {
        get => _remainingFiles;
        set { _remainingFiles = value; OnPropertyChanged(nameof(RemainingFiles)); }
    }

    public string SpeedText
    {
        get => _speedText;
        set { _speedText = value; OnPropertyChanged(nameof(SpeedText)); }
    }

    public string EtaText
    {
        get => _etaText;
        set { _etaText = value; OnPropertyChanged(nameof(EtaText)); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(nameof(StatusMessage)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public SyncWindow()
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

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        var profileName = ProfileComboBox.SelectedItem as string;
        var bucketName = BucketComboBox.SelectedItem as string;
        var localFolder = LocalFolderTextBox.Text?.Trim() ?? string.Empty;
        var prefixInput = PrefixTextBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(bucketName))
        {
            System.Windows.MessageBox.Show("Selecione um perfil e um bucket.");
            return;
        }

        if (string.IsNullOrWhiteSpace(localFolder))
        {
            System.Windows.MessageBox.Show("Selecione a pasta local.");
            return;
        }

        SyncButton.IsEnabled = false;
        ProgressPercent = 0;
        TotalFiles = 0;
        DownloadedFiles = 0;
        SkippedFiles = 0;
        RemainingFiles = 0;
        SpeedText = "0 B/s";
        EtaText = "-";
        StatusMessage = "Listando objetos...";

        try
        {
            if (!int.TryParse(ThreadsTextBox.Text, out var threads) || threads < 1 || threads > 64)
            {
                System.Windows.MessageBox.Show("Informe um numero de threads entre 1 e 64.");
                return;
            }

            var credentials = _profileManager.GetCredentials(profileName);
            var region = _profileManager.GetRegion(profileName);
            using var client = new AmazonS3Client(credentials, region);
            var service = new S3SyncService(client);

            if (!TryParsePrefixInput(prefixInput, bucketName, out var parsedBucket, out var normalizedPrefix, out var prefixError))
            {
                System.Windows.MessageBox.Show(prefixError);
                return;
            }

            if (!string.Equals(parsedBucket, bucketName, StringComparison.Ordinal))
            {
                System.Windows.MessageBox.Show("O prefixo informado pertence a outro bucket. Selecione o bucket correto.");
                return;
            }

            if (!await PrefixExistsAsync(client, bucketName, normalizedPrefix))
            {
                System.Windows.MessageBox.Show("Prefixo nao encontrado no bucket. Ajuste o prefixo ou selecione pela lista.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var progress = new Progress<S3SyncProgress>(p =>
            {
                TotalFiles = p.Total;
                DownloadedFiles = p.Downloaded;
                SkippedFiles = p.Skipped;
                RemainingFiles = p.Remaining;
                ProgressPercent = p.Percent;
                var seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                var bytesPerSecond = p.DownloadedBytes / seconds;
                SpeedText = $"{FormatBytes(bytesPerSecond)}/s";
                if (bytesPerSecond > 0)
                {
                    var etaSeconds = p.RemainingBytes / bytesPerSecond;
                    EtaText = TimeSpan.FromSeconds(etaSeconds).ToString("hh\\:mm\\:ss");
                }
                else
                {
                    EtaText = "-";
                }
                StatusMessage = $"Processando... {p.Processed}/{p.Total}";
            });

            var summary = await service.SyncAsync(bucketName, normalizedPrefix, localFolder, threads, progress);

            StatusMessage = $"Concluido. Baixados {summary.Downloaded}, ignorados {summary.Skipped}, total {summary.Total}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private void BrowseLocalButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Selecione a pasta local para sincronizar",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            LocalFolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void BrowsePrefixButton_Click(object sender, RoutedEventArgs e)
    {
        var profileName = ProfileComboBox.SelectedItem as string;
        var bucketName = BucketComboBox.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(bucketName))
        {
            System.Windows.MessageBox.Show("Selecione um perfil e um bucket.");
            return;
        }

        try
        {
            var credentials = _profileManager.GetCredentials(profileName);
            var region = _profileManager.GetRegion(profileName);
            using var client = new AmazonS3Client(credentials, region);

            var picker = new S3PrefixPickerWindow(client, bucketName) { Owner = this };
            if (picker.ShowDialog() == true)
            {
                PrefixTextBox.Text = picker.SelectedPrefix;
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatBytes(double bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.0} {units[unit]}";
    }

    private static bool TryParsePrefixInput(
        string input,
        string selectedBucket,
        out string bucket,
        out string normalizedPrefix,
        out string error)
    {
        bucket = selectedBucket;
        normalizedPrefix = S3SyncService.NormalizePrefix(input);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            normalizedPrefix = string.Empty;
            return true;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = trimmed["s3://".Length..];
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex <= 0)
            {
                error = "Prefixo invalido. Use s3://bucket/pasta ou somente a pasta.";
                return false;
            }

            bucket = remainder[..slashIndex];
            var key = remainder[(slashIndex + 1)..];
            normalizedPrefix = S3SyncService.NormalizePrefix(key);
            return true;
        }

        if (trimmed.StartsWith("arn:aws:s3:::", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = trimmed["arn:aws:s3:::".Length..];
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex < 0)
            {
                bucket = remainder;
                normalizedPrefix = string.Empty;
                return true;
            }

            bucket = remainder[..slashIndex];
            var key = remainder[(slashIndex + 1)..];
            normalizedPrefix = S3SyncService.NormalizePrefix(key);
            return true;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            error = "Prefixo invalido. Use somente o caminho dentro do bucket.";
            return false;
        }

        normalizedPrefix = S3SyncService.NormalizePrefix(trimmed);
        return true;
    }

    private static async Task<bool> PrefixExistsAsync(IAmazonS3 s3, string bucketName, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return true;
        }

        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = prefix,
            MaxKeys = 1
        };

        var response = await s3.ListObjectsV2Async(request);
        return response.S3Objects.Any();
    }
}
