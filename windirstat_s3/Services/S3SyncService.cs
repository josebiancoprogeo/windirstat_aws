using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;

namespace windirstat_s3.Services;

public sealed class S3SyncService
{
    private const int PendingValidationBuffer = 1000;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(250);
    private readonly IAmazonS3 _s3;

    public S3SyncService(IAmazonS3 s3)
    {
        _s3 = s3;
    }

    public async Task<S3SyncSummary> SyncAsync(
        string bucketName,
        string prefix,
        string localRoot,
        int maxConcurrency,
        SyncConcurrencyController? concurrencyController = null,
        IEnumerable<string>? ignoredFileNames = null,
        IEnumerable<string>? ignoredRootNames = null,
        IProgress<S3SyncProgress>? progress = null,
        IProgress<string>? logProgress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPrefix = NormalizePrefix(prefix);
        var ignoredNames = new HashSet<string>(ignoredFileNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var ignoredRoots = new HashSet<string>(ignoredRootNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(localRoot))
        {
            Directory.CreateDirectory(localRoot);
        }

        var discoveredObjects = 0;
        long discoveredBytes = 0;
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        long downloadedBytes = 0;
        long skippedBytes = 0;
        long failedBytes = 0;
        var pendingValidation = 0;
        var queuedForDownload = 0;
        long queuedDownloadBytes = 0;
        var localScanCompleted = 0;
        var remoteListingCompleted = 0;
        var localNames = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var pendingLock = new object();
        var pendingByName = new Dictionary<string, List<PendingCandidate>>(StringComparer.OrdinalIgnoreCase);
        var pendingOrder = new Queue<PendingCandidate>();
        var reportLock = new object();
        var lastReportedAt = DateTime.UtcNow - ProgressReportInterval;

        void ReportProgress(bool force = false)
        {
            if (progress == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            lock (reportLock)
            {
                if (!force && now - lastReportedAt < ProgressReportInterval)
                {
                    return;
                }

                lastReportedAt = now;
            }

            progress.Report(new S3SyncProgress(
                Volatile.Read(ref discoveredObjects),
                Volatile.Read(ref downloaded),
                Volatile.Read(ref skipped),
                Volatile.Read(ref failed),
                Volatile.Read(ref discoveredBytes),
                Volatile.Read(ref downloadedBytes),
                Volatile.Read(ref skippedBytes),
                Volatile.Read(ref failedBytes),
                Volatile.Read(ref pendingValidation),
                Volatile.Read(ref queuedForDownload),
                Volatile.Read(ref queuedDownloadBytes),
                Volatile.Read(ref localScanCompleted) == 1,
                Volatile.Read(ref remoteListingCompleted) == 1));
        }

        void Log(string message)
        {
            logProgress?.Report($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        void MarkSkipped(S3Object s3Object)
        {
            Interlocked.Increment(ref skipped);
            Interlocked.Add(ref skippedBytes, s3Object.Size.GetValueOrDefault());
            ReportProgress();
        }

        void MarkFailed(S3Object s3Object, Exception ex)
        {
            Interlocked.Increment(ref failed);
            Interlocked.Add(ref failedBytes, s3Object.Size.GetValueOrDefault());
            Log($"Erro em '{s3Object.Key}': {ex.Message}");
            ReportProgress(true);
        }

        void RemoveFromDownloadQueue(S3Object s3Object)
        {
            Interlocked.Decrement(ref queuedForDownload);
            Interlocked.Add(ref queuedDownloadBytes, -s3Object.Size.GetValueOrDefault());
        }

        var concurrency = Math.Clamp(maxConcurrency, 1, 64);
        var activeConcurrency = concurrencyController ?? new SyncConcurrencyController(concurrency);
        var workerPoolSize = concurrencyController == null ? concurrency : 64;
        var downloadChannel = Channel.CreateUnbounded<S3Object>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        async Task QueueForDownloadAsync(S3Object s3Object, bool wasPending)
        {
            if (wasPending)
            {
                Interlocked.Decrement(ref pendingValidation);
            }

            Interlocked.Increment(ref queuedForDownload);
            Interlocked.Add(ref queuedDownloadBytes, s3Object.Size.GetValueOrDefault());
            await downloadChannel.Writer.WriteAsync(s3Object, cancellationToken);
            ReportProgress();
        }

        PendingCandidate? DequeuePendingForDownload()
        {
            lock (pendingLock)
            {
                while (pendingOrder.Count > 0)
                {
                    var candidate = pendingOrder.Dequeue();
                    if (candidate.Resolved)
                    {
                        continue;
                    }

                    candidate.Resolved = true;
                    if (pendingByName.TryGetValue(candidate.FileName, out var pendingList))
                    {
                        pendingList.Remove(candidate);
                        if (pendingList.Count == 0)
                        {
                            pendingByName.Remove(candidate.FileName);
                        }
                    }

                    return candidate;
                }
            }

            return null;
        }

        async Task DrainPendingBufferAsync()
        {
            while (Volatile.Read(ref pendingValidation) > PendingValidationBuffer)
            {
                var candidate = DequeuePendingForDownload();
                if (candidate == null)
                {
                    break;
                }

                await QueueForDownloadAsync(candidate.Object, wasPending: true);
            }
        }

        void ScanLocalFiles()
        {
            foreach (var file in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(name) || !localNames.TryAdd(name, 0))
                {
                    continue;
                }

                List<PendingCandidate>? matchedPending = null;
                lock (pendingLock)
                {
                    if (pendingByName.Remove(name, out var pendingForName))
                    {
                        matchedPending = pendingForName;
                    }
                }

                if (matchedPending == null)
                {
                    continue;
                }

                foreach (var pendingObject in matchedPending)
                {
                    if (pendingObject.Resolved)
                    {
                        continue;
                    }

                    pendingObject.Resolved = true;
                    Interlocked.Decrement(ref pendingValidation);
                    MarkSkipped(pendingObject.Object);
                }
            }

            Volatile.Write(ref localScanCompleted, 1);
            ReportProgress(true);
        }

        async Task DownloadWorkerAsync()
        {
            await foreach (var s3Object in downloadChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await activeConcurrency.WaitAsync(cancellationToken);
                try
                {
                    var relativeKey = normalizedPrefix.Length > 0 && s3Object.Key.StartsWith(normalizedPrefix, StringComparison.Ordinal)
                        ? s3Object.Key[normalizedPrefix.Length..]
                        : s3Object.Key;

                    var localPath = BuildLocalPath(localRoot, relativeKey);
                    var fileName = Path.GetFileName(s3Object.Key);

                    if (Volatile.Read(ref localScanCompleted) == 0)
                    {
                        var shouldSkip = !string.IsNullOrWhiteSpace(fileName) && localNames.ContainsKey(fileName);
                        if (!shouldSkip)
                        {
                            shouldSkip = File.Exists(localPath);
                        }

                        if (shouldSkip)
                        {
                            RemoveFromDownloadQueue(s3Object);
                            MarkSkipped(s3Object);
                            continue;
                        }
                    }

                    var localDir = Path.GetDirectoryName(localPath);
                    if (!string.IsNullOrWhiteSpace(localDir))
                    {
                        Directory.CreateDirectory(localDir);
                    }

                    using var getResponse = await _s3.GetObjectAsync(bucketName, s3Object.Key, cancellationToken);
                    await using var output = new FileStream(
                        localPath,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Create,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            BufferSize = 1024 * 256,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                        });

                    await getResponse.ResponseStream.CopyToAsync(output, 1024 * 256, cancellationToken);

                    Interlocked.Increment(ref downloaded);
                    Interlocked.Add(ref downloadedBytes, s3Object.Size.GetValueOrDefault());
                    ReportProgress();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RemoveFromDownloadQueue(s3Object);
                    MarkFailed(s3Object, ex);
                }
                finally
                {
                    activeConcurrency.Release();
                }
            }
        }

        async Task ProcessRemoteObjectAsync(S3Object s3Object)
        {
            var fileName = Path.GetFileName(s3Object.Key);
            if (!string.IsNullOrWhiteSpace(fileName) && ignoredNames.Contains(fileName))
            {
                MarkSkipped(s3Object);
                return;
            }

            var rootName = GetRelativeRootName(normalizedPrefix, s3Object.Key);
            if (!string.IsNullOrWhiteSpace(rootName) && ignoredRoots.Contains(rootName))
            {
                MarkSkipped(s3Object);
                return;
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                await QueueForDownloadAsync(s3Object, wasPending: false);
                return;
            }

            var skipImmediately = false;
            var queueImmediately = false;
            lock (pendingLock)
            {
                if (localNames.ContainsKey(fileName))
                {
                    skipImmediately = true;
                }
                else if (Volatile.Read(ref localScanCompleted) == 1)
                {
                    queueImmediately = true;
                }
                else
                {
                    if (!pendingByName.TryGetValue(fileName, out var pendingList))
                    {
                        pendingList = new List<PendingCandidate>();
                        pendingByName[fileName] = pendingList;
                    }

                    var candidate = new PendingCandidate(s3Object, fileName);
                    pendingList.Add(candidate);
                    pendingOrder.Enqueue(candidate);
                    Interlocked.Increment(ref pendingValidation);
                }
            }

            if (skipImmediately)
            {
                MarkSkipped(s3Object);
            }
            else if (queueImmediately)
            {
                await QueueForDownloadAsync(s3Object, wasPending: false);
            }
            else
            {
                ReportProgress();
                await DrainPendingBufferAsync();
            }
        }

        var workerTasks = new Task[workerPoolSize];
        for (var i = 0; i < workerPoolSize; i++)
        {
            workerTasks[i] = Task.Run(DownloadWorkerAsync, cancellationToken);
        }

        var localScanTask = Task.Run(ScanLocalFiles, cancellationToken);
        ReportProgress(true);

        try
        {
            var request = new ListObjectsV2Request
            {
                BucketName = bucketName,
                Prefix = normalizedPrefix
            };

            ListObjectsV2Response response;
            do
            {
                response = await _s3.ListObjectsV2Async(request, cancellationToken);

                foreach (var s3Object in response.S3Objects)
                {
                    if (s3Object.Key.EndsWith('/'))
                    {
                        continue;
                    }

                    Interlocked.Increment(ref discoveredObjects);
                    Interlocked.Add(ref discoveredBytes, s3Object.Size.GetValueOrDefault());
                    await ProcessRemoteObjectAsync(s3Object);
                }

                request.ContinuationToken = response.NextContinuationToken;

            } while (response.IsTruncated.GetValueOrDefault());

            await localScanTask;
            Volatile.Write(ref remoteListingCompleted, 1);

            List<PendingCandidate>? remainingPending = null;
            lock (pendingLock)
            {
                if (pendingOrder.Count > 0)
                {
                    remainingPending = new List<PendingCandidate>(pendingOrder.Count);
                    while (pendingOrder.Count > 0)
                    {
                        var candidate = pendingOrder.Dequeue();
                        if (!candidate.Resolved)
                        {
                            candidate.Resolved = true;
                            remainingPending.Add(candidate);
                        }
                    }

                    pendingByName.Clear();
                }
            }

            if (remainingPending != null)
            {
                remainingPending.Sort((left, right) =>
                    Nullable.Compare(right.Object.LastModified, left.Object.LastModified));

                foreach (var pendingObject in remainingPending)
                {
                    await QueueForDownloadAsync(pendingObject.Object, wasPending: true);
                }
            }

            downloadChannel.Writer.Complete();
            await Task.WhenAll(workerTasks);

            ReportProgress(true);
            return new S3SyncSummary(
                Volatile.Read(ref downloaded),
                Volatile.Read(ref skipped),
                Volatile.Read(ref failed),
                Volatile.Read(ref discoveredObjects));
        }
        catch (Exception ex)
        {
            Log($"Erro fatal: {ex.Message}");
            downloadChannel.Writer.TryComplete(ex);
            throw;
        }
    }

    public static string NormalizePrefix(string? prefix)
    {
        var trimmed = (prefix ?? string.Empty).Trim();
        if (trimmed.StartsWith('/'))
        {
            trimmed = trimmed.TrimStart('/');
        }

        if (trimmed.Length > 0 && !trimmed.EndsWith('/'))
        {
            trimmed += "/";
        }

        return trimmed;
    }

    private static string BuildLocalPath(string localRoot, string relativeKey)
    {
        var normalized = (relativeKey ?? string.Empty)
            .Replace('\\', '/')
            .TrimStart('/');

        var parts = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length == 0
            ? localRoot
            : Path.Combine(localRoot, Path.Combine(parts));
    }

    private static string? GetRelativeRootName(string normalizedPrefix, string key)
    {
        var relativeKey = normalizedPrefix.Length > 0 && key.StartsWith(normalizedPrefix, StringComparison.Ordinal)
            ? key[normalizedPrefix.Length..]
            : key;

        if (string.IsNullOrWhiteSpace(relativeKey))
        {
            return null;
        }

        var slashIndex = relativeKey.IndexOf('/');
        var rootName = slashIndex >= 0 ? relativeKey[..slashIndex] : relativeKey;
        return string.IsNullOrWhiteSpace(rootName) ? null : rootName;
    }
}

internal sealed class PendingCandidate
{
    public PendingCandidate(S3Object s3Object, string fileName)
    {
        Object = s3Object;
        FileName = fileName;
    }

    public S3Object Object { get; }
    public string FileName { get; }
    public bool Resolved { get; set; }
}

public sealed record S3SyncProgress(
    int Total,
    int Downloaded,
    int Skipped,
    int Failed,
    long TotalBytes,
    long DownloadedBytes,
    long SkippedBytes,
    long FailedBytes,
    int PendingValidation,
    int QueuedForDownload,
    long QueuedDownloadBytes,
    bool LocalScanCompleted,
    bool RemoteListingCompleted)
{
    public int Processed => Downloaded + Skipped + Failed;
    public int Remaining => Math.Max(0, Total - Processed);
    public double Percent => Total == 0 ? 0 : (double)Processed / Total * 100;
    public int RemainingDownloads => Math.Max(0, QueuedForDownload - Downloaded - Failed);
    public long RemainingDownloadBytes => Math.Max(0, QueuedDownloadBytes - DownloadedBytes - FailedBytes);
}

public sealed record S3SyncSummary(int Downloaded, int Skipped, int Failed, int Total);
