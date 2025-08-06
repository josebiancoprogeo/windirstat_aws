using Amazon.S3;
using Amazon.S3.Model;
using System.IO;

namespace windirstat_s3.Services;

public class FolderNode
{
    public string Name { get; }
    public long Size { get; set; }
    public Dictionary<string, FolderNode> Children { get; } = new();
    public Dictionary<string, ExtensionInfo> Extensions { get; } = new();

    public FolderNode(string name)
    {
        Name = name;
    }
}

public class S3Scanner
{
    private readonly IAmazonS3 _s3;

    public S3Scanner(IAmazonS3 s3)
    {
        _s3 = s3;
    }

    public async Task<FolderNode> ScanAsync(string bucketName, IEnumerable<string>? ignorePrefixes = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var prefixes = ignorePrefixes?.ToList() ?? new List<string>();
        var root = new FolderNode(bucketName);

        var totalObjects = 0;
        if (progress != null)
        {
            var countRequest = new ListObjectsV2Request
            {
                BucketName = bucketName
            };

            ListObjectsV2Response countResponse;
            do
            {
                countResponse = await _s3.ListObjectsV2Async(countRequest, cancellationToken);

                foreach (var s3Object in countResponse.S3Objects)
                {
                    if (s3Object.Key.EndsWith('/'))
                    {
                        continue;
                    }

                    if (prefixes.Any(p => s3Object.Key.StartsWith(p)))
                    {
                        continue;
                    }

                    totalObjects++;
                }

                countRequest.ContinuationToken = countResponse.NextContinuationToken;

            } while (countResponse.IsTruncated.GetValueOrDefault());

            if (totalObjects == 0)
            {
                progress.Report(100);
                return root;
            }
        }

        var request = new ListObjectsV2Request
        {
            BucketName = bucketName
        };

        var processed = 0;
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

                if (prefixes.Any(p => s3Object.Key.StartsWith(p)))
                {
                    continue;
                }

                AddObject(root, s3Object.Key, s3Object.Size.GetValueOrDefault());
                processed++;
                progress?.Report((double)processed / totalObjects * 100);
            }

            request.ContinuationToken = response.NextContinuationToken;

        } while (response.IsTruncated.GetValueOrDefault());

        progress?.Report(100);
        return root;
    }

    private static void AddObject(FolderNode root, string key, long size)
    {
        root.Size += size;
        var node = root;

        var extension = Path.GetExtension(key).ToLowerInvariant();
        UpdateExtension(node, extension, size);

        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (!node.Children.TryGetValue(part, out var child))
            {
                child = new FolderNode(part);
                node.Children[part] = child;
            }

            child.Size += size;
            node = child;

            UpdateExtension(node, extension, size);
        }
    }

    private static void UpdateExtension(FolderNode node, string extension, long size)
    {
        if (!node.Extensions.TryGetValue(extension, out var info))
        {
            info = new ExtensionInfo();
            node.Extensions[extension] = info;
        }
        info.Count++;
        info.Size += size;
    }
}

