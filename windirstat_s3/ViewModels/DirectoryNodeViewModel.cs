using System;
using System.Collections.ObjectModel;
using System.Linq;
using windirstat_s3.Services;

namespace windirstat_s3.ViewModels;

public class DirectoryNodeViewModel
{
    private readonly long _totalSize;

    public string Name { get; }
    public long Size { get; }
    public long OwnSize { get; }
    public long FileCount { get; }
    public DateTime LastModified { get; }
    public ObservableCollection<DirectoryNodeViewModel> Children { get; } = new();

    public DirectoryNodeViewModel(FolderNode node, long totalSize)
    {
        _totalSize = totalSize;
        Name = node.Name;
        Size = node.Size;
        OwnSize = node.OwnSize;
        FileCount = node.FileCount;
        LastModified = node.LastModified;
        foreach (var child in node.Children.Values.OrderByDescending(c => c.Size))
        {
            Children.Add(new DirectoryNodeViewModel(child, totalSize));
        }
    }

    public double SubtreePercent => _totalSize == 0 ? 0 : (double)Size / _totalSize;
    public double PercentOfTotal => _totalSize == 0 ? 0 : (double)OwnSize / _totalSize;
    public long TotalFiles => FileCount;
    public int TotalSubdirs => Children.Count;
    public long TotalItems => TotalFiles + TotalSubdirs;
    public string Attributes => string.Empty;
}
