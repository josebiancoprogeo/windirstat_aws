using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;

namespace windirstat_s3.Services;

public sealed class S3SyncService
{
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
        IProgress<S3SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPrefix = NormalizePrefix(prefix);

        if (!Directory.Exists(localRoot))
        {
            Directory.CreateDirectory(localRoot);
        }

        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!string.IsNullOrWhiteSpace(name))
            {
                localNames.Add(name);
            }
        }

        var (totalObjects, totalBytes) = await CountObjectsAsync(bucketName, normalizedPrefix, cancellationToken);
        if (totalObjects == 0)
        {
            progress?.Report(new S3SyncProgress(0, 0, 0, 0, 0, 0));
            return new S3SyncSummary(0, 0, 0);
        }

        var downloaded = 0;
        var skipped = 0;
        long downloadedBytes = 0;
        long skippedBytes = 0;
        progress?.Report(new S3SyncProgress(totalObjects, downloaded, skipped, totalBytes, downloadedBytes, skippedBytes));

        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = normalizedPrefix
        };

        var concurrency = Math.Clamp(maxConcurrency, 1, 64);
        using var downloadSemaphore = new SemaphoreSlim(concurrency, concurrency);
        var downloadTasks = new List<Task>(256);

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

                var fileName = Path.GetFileName(s3Object.Key);
                if (!string.IsNullOrWhiteSpace(fileName) && localNames.Contains(fileName))
                {
                    Interlocked.Increment(ref skipped);
                    Interlocked.Add(ref skippedBytes, s3Object.Size.GetValueOrDefault());
                    progress?.Report(new S3SyncProgress(totalObjects, downloaded, skipped, totalBytes, downloadedBytes, skippedBytes));
                }
                else
                {
                    await downloadSemaphore.WaitAsync(cancellationToken);
                    downloadTasks.Add(DownloadObjectAsync(
                        s3Object,
                        bucketName,
                        normalizedPrefix,
                        localRoot,
                        downloadSemaphore,
                        () =>
                        {
                            Interlocked.Increment(ref downloaded);
                            Interlocked.Add(ref downloadedBytes, s3Object.Size.GetValueOrDefault());
                            progress?.Report(new S3SyncProgress(totalObjects, downloaded, skipped, totalBytes, downloadedBytes, skippedBytes));
                        },
                        cancellationToken));

                    if (downloadTasks.Count > 1024)
                    {
                        downloadTasks.RemoveAll(t => t.IsCompleted);
                    }
                }
            }

            request.ContinuationToken = response.NextContinuationToken;

        } while (response.IsTruncated.GetValueOrDefault());

        if (downloadTasks.Count > 0)
        {
            await Task.WhenAll(downloadTasks);
        }

        progress?.Report(new S3SyncProgress(totalObjects, downloaded, skipped, totalBytes, downloadedBytes, skippedBytes));
        return new S3SyncSummary(downloaded, skipped, totalObjects);
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

    private async Task<(int TotalObjects, long TotalBytes)> CountObjectsAsync(string bucketName, string prefix, CancellationToken cancellationToken)
    {
        var total = 0;
        long totalBytes = 0;
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = prefix
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3.ListObjectsV2Async(request, cancellationToken);

            foreach (var s3Object in response.S3Objects)
            {
                if (!s3Object.Key.EndsWith('/'))
                {
                    total++;
                    totalBytes += s3Object.Size.GetValueOrDefault();
                }
            }

            request.ContinuationToken = response.NextContinuationToken;

        } while (response.IsTruncated.GetValueOrDefault());

        return (total, totalBytes);
    }

    private async Task DownloadObjectAsync(
        S3Object s3Object,
        string bucketName,
        string normalizedPrefix,
        string localRoot,
        SemaphoreSlim downloadSemaphore,
        Action onCompleted,
        CancellationToken cancellationToken)
    {
        try
        {
            var relativeKey = normalizedPrefix.Length > 0 && s3Object.Key.StartsWith(normalizedPrefix, StringComparison.Ordinal)
                ? s3Object.Key[normalizedPrefix.Length..]
                : s3Object.Key;

            var localPath = BuildLocalPath(localRoot, relativeKey);
            var localDir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(localDir))
            {
                Directory.CreateDirectory(localDir);
            }

            using var getResponse = await _s3.GetObjectAsync(bucketName, s3Object.Key, cancellationToken);
            await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await getResponse.ResponseStream.CopyToAsync(output, cancellationToken);
            onCompleted();
        }
        finally
        {
            downloadSemaphore.Release();
        }
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
}

public sealed record S3SyncProgress(int Total, int Downloaded, int Skipped, long TotalBytes, long DownloadedBytes, long SkippedBytes)
{
    public int Processed => Downloaded + Skipped;
    public int Remaining => Math.Max(0, Total - Processed);
    public double Percent => Total == 0 ? 0 : (double)Processed / Total * 100;
    public long RemainingBytes => Math.Max(0, TotalBytes - DownloadedBytes - SkippedBytes);
}

public sealed record S3SyncSummary(int Downloaded, int Skipped, int Total);
