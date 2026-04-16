using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon;
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
    private int _analyzedFiles;
    private int _remainingFiles;
    private string _speedText = "0 B/s";
    private string _etaText = "-";
    private string _progressLabel = "0%";
    private bool _isProgressIndeterminate;
    private Stopwatch? _downloadStopwatch;
    private SyncConcurrencyController? _syncConcurrencyController;
    private readonly StringBuilder _errorLog = new();
    private string? _loadedIgnoredEntriesFilePath;
    private List<string> _loadedIgnoredEntries = new();
    private HashSet<string> _ignoredRootNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _ignoredLoadedFileNames = new(StringComparer.OrdinalIgnoreCase);

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

    public int AnalyzedFiles
    {
        get => _analyzedFiles;
        set { _analyzedFiles = value; OnPropertyChanged(nameof(AnalyzedFiles)); }
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

    public string ProgressLabel
    {
        get => _progressLabel;
        set { _progressLabel = value; OnPropertyChanged(nameof(ProgressLabel)); }
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        set { _isProgressIndeterminate = value; OnPropertyChanged(nameof(IsProgressIndeterminate)); }
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
        AnalyzedFiles = 0;
        RemainingFiles = 0;
        SpeedText = "0 B/s";
        EtaText = "-";
        ProgressLabel = "Preparando...";
        IsProgressIndeterminate = true;
        _downloadStopwatch = null;
        _errorLog.Clear();
        ErrorLogTextBox.Clear();
        StatusMessage = "Listando objetos...";

        try
        {
            if (!int.TryParse(ThreadsTextBox.Text, out var threads) || threads < 1 || threads > 64)
            {
                System.Windows.MessageBox.Show("Informe um numero de threads entre 1 e 64.");
                return;
            }

            _syncConcurrencyController = new SyncConcurrencyController(threads);

            var credentials = _profileManager.GetCredentials(profileName);
            var region = _profileManager.GetRegion(profileName);
            using var client = CreateS3Client(credentials, region);
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

            var ignoredFileNames = ParseIgnoredFileNames(IgnoreFilesTextBox.Text);
            ignoredFileNames.UnionWith(_ignoredLoadedFileNames);
            if (_ignoredRootNames.Count > 0)
            {
                AppendErrorLog($"[{DateTime.Now:HH:mm:ss}] Ignorando {_ignoredRootNames.Count} raiz(es) carregada(s) do arquivo de pastas ignoradas.");
            }
            else if (_ignoredLoadedFileNames.Count > 0)
            {
                AppendErrorLog($"[{DateTime.Now:HH:mm:ss}] Ignorando {_ignoredLoadedFileNames.Count} arquivo(s) carregado(s) do arquivo de pastas ignoradas.");
            }

            var progress = new Progress<S3SyncProgress>(p =>
            {
                TotalFiles = p.Total;
                DownloadedFiles = p.Downloaded;
                SkippedFiles = p.Skipped;
                AnalyzedFiles = p.Skipped + p.QueuedForDownload + p.Failed;
                RemainingFiles = p.RemainingDownloads + p.PendingValidation;
                IsProgressIndeterminate = !p.RemoteListingCompleted;

                if (p.RemoteListingCompleted)
                {
                    var effectiveTotal = Math.Max(1, p.QueuedForDownload + p.Skipped + p.Failed);
                    ProgressPercent = (double)(p.Downloaded + p.Skipped + p.Failed) / effectiveTotal * 100;
                    ProgressLabel = $"{ProgressPercent:F1}%";
                }
                else
                {
                    ProgressPercent = 0;
                    ProgressLabel = $"Descobertos: {p.Total}";
                }

                if (p.QueuedDownloadBytes > 0 && _downloadStopwatch == null)
                {
                    _downloadStopwatch = Stopwatch.StartNew();
                }

                var seconds = Math.Max(0.001, _downloadStopwatch?.Elapsed.TotalSeconds ?? 0);
                var bytesPerSecond = _downloadStopwatch == null ? 0 : p.DownloadedBytes / seconds;
                SpeedText = $"{FormatBytes(bytesPerSecond)}/s";

                if (bytesPerSecond > 0 && p.RemainingDownloadBytes > 0)
                {
                    var etaSeconds = p.RemainingDownloadBytes / bytesPerSecond;
                    EtaText = TimeSpan.FromSeconds(etaSeconds).ToString("hh\\:mm\\:ss");
                }
                else if (p.QueuedDownloadBytes > 0 && p.RemainingDownloadBytes == 0)
                {
                    EtaText = "00:00:00";
                }
                else
                {
                    EtaText = "-";
                }

                StatusMessage = !p.LocalScanCompleted || !p.RemoteListingCompleted
                    ? $"Validando duplicados... {p.Processed}/{p.Total} | pendentes {p.PendingValidation}"
                    : $"Baixando... {p.Downloaded}/{p.QueuedForDownload} | falhas {p.Failed}";
            });

            var logProgress = new Progress<string>(AppendErrorLog);
            var summary = await service.SyncAsync(bucketName, normalizedPrefix, localFolder, threads, _syncConcurrencyController, ignoredFileNames, _ignoredRootNames, progress, logProgress);

            IsProgressIndeterminate = false;
            ProgressPercent = 100;
            ProgressLabel = "100,0%";
            StatusMessage = $"Processo concluido com sucesso. Baixados {summary.Downloaded}, ignorados {summary.Skipped}, falhas {summary.Failed}, total {summary.Total}.";
        }
        catch (Exception ex)
        {
            AppendErrorLog($"[{DateTime.Now:HH:mm:ss}] Erro fatal: {ex.Message}");
            System.Windows.MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _syncConcurrencyController = null;
            SyncButton.IsEnabled = true;
        }
    }

    private void ThreadsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncConcurrencyController == null)
        {
            return;
        }

        if (int.TryParse(ThreadsTextBox.Text, out var threads) && threads >= 1 && threads <= 64)
        {
            _syncConcurrencyController.UpdateTargetConcurrency(threads);
            StatusMessage = $"Ajustando concorrencia para {threads} threads...";
        }
    }

    private void AppendErrorLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _errorLog.AppendLine(message);
        ErrorLogTextBox.Text = _errorLog.ToString();
        ErrorLogTextBox.ScrollToEnd();
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

    private void BrowseIgnoredFoldersFileButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Selecione o arquivo de pastas ignoradas",
            Filter = "Arquivos de texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        try
        {
            _loadedIgnoredEntriesFilePath = dialog.FileName;
            _loadedIgnoredEntries = File.ReadLines(dialog.FileName)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            IgnoredFoldersFileTextBox.Text = dialog.FileName;
            RefreshLoadedIgnoreEntries();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void IgnoreModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadedIgnoredEntries.Count == 0)
        {
            return;
        }

        RefreshLoadedIgnoreEntries();
    }

    private void BrowsePrefixButton_Click(object sender, RoutedEventArgs e)
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
            using var client = CreateS3Client(credentials, region);

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
            using var client = CreateS3Client(credentials, region);
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

    private static AmazonS3Client CreateS3Client(Amazon.Runtime.AWSCredentials credentials, RegionEndpoint region)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = region,
            MaxConnectionsPerServer = 256,
            BufferSize = 1024 * 256
        };

        return new AmazonS3Client(credentials, config);
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

    private static HashSet<string> ParseIgnoredFileNames(string? multiLineText)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(multiLineText))
        {
            return result;
        }

        var lines = multiLineText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                result.Add(line);
            }
        }

        return result;
    }

    private void RefreshLoadedIgnoreEntries()
    {
        var ignoreByDirectory = IgnoreByDirectoryCheckBox.IsChecked != false;
        _ignoredRootNames = ignoreByDirectory
            ? ExtractIgnoredRootNames(_loadedIgnoredEntries)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _ignoredLoadedFileNames = ignoreByDirectory
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : ExtractIgnoredFileNames(_loadedIgnoredEntries);

        if (string.IsNullOrWhiteSpace(_loadedIgnoredEntriesFilePath))
        {
            return;
        }

        var ignoredCount = ignoreByDirectory ? _ignoredRootNames.Count : _ignoredLoadedFileNames.Count;
        var ignoredKind = ignoreByDirectory ? "raiz(es)" : "arquivo(s)";
        var modeLabel = ignoreByDirectory ? "diretorio raiz" : "arquivo";
        AppendErrorLog($"[{DateTime.Now:HH:mm:ss}] Arquivo carregado em modo {modeLabel}: {ignoredCount} {ignoredKind} para ignorar.");
    }

    private static HashSet<string> ExtractIgnoredRootNames(IEnumerable<string> lines)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();

        if (normalizedLines.Count == 0)
        {
            return result;
        }

        var commonBaseSegments = GetCommonBaseSegments(normalizedLines);
        foreach (var line in normalizedLines)
        {
            var root = ExtractRootNameFromLoadedLine(line, commonBaseSegments);
            if (!string.IsNullOrWhiteSpace(root))
            {
                result.Add(root);
            }
        }

        return result;
    }

    private static HashSet<string> ExtractIgnoredFileNames(IEnumerable<string> lines)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (!LooksLikeFilePath(line))
            {
                continue;
            }

            var fileName = Path.GetFileName(line.Trim());
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                result.Add(fileName);
            }
        }

        return result;
    }

    private static string? ExtractRootNameFromLoadedLine(string line, IReadOnlyList<string> commonBaseSegments)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var segments = SplitPathSegments(line);
        if (segments.Count == 0)
        {
            return null;
        }

        return segments.Count > commonBaseSegments.Count
            ? segments[commonBaseSegments.Count]
            : segments[^1];
    }

    private static List<string> GetCommonBaseSegments(IReadOnlyList<string> lines)
    {
        var splitLines = lines
            .Select(SplitPathSegments)
            .Where(segments => segments.Count > 0)
            .ToList();

        if (splitLines.Count == 0)
        {
            return new List<string>();
        }

        var common = new List<string>();
        var minLength = splitLines.Min(segments => segments.Count);
        for (var i = 0; i < minLength; i++)
        {
            var candidate = splitLines[0][i];
            if (splitLines.All(segments => string.Equals(segments[i], candidate, StringComparison.OrdinalIgnoreCase)))
            {
                common.Add(candidate);
                continue;
            }

            break;
        }

        return common;
    }

    private static List<string> SplitPathSegments(string path)
    {
        return path.Trim()
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static bool LooksLikeFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (File.Exists(trimmed))
        {
            return true;
        }

        if (Directory.Exists(trimmed))
        {
            return false;
        }

        return Path.HasExtension(trimmed);
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
