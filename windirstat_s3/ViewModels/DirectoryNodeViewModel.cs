using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windirstat.Core.Models;

namespace windirstat_s3.ViewModels;

public class DirectoryNodeViewModel
{
    private readonly long _totalSize;
    private readonly long _parentSize;

    public string Name { get; }
    public long Size { get; }
    public string FriendlySize => FormatBytes(Size);
    public long OwnSize { get; }
    public long FileCount { get; }
    public DateTime LastModified { get; }
    public ObservableCollection<DirectoryNodeViewModel> Children { get; } = new();

    public bool IsFile { get; }
    public int Depth { get; }

    public DirectoryNodeViewModel(FolderNode node, long totalSize, long parentSize = 0, int depth = 0)
    {
        _totalSize = totalSize;
        _parentSize = parentSize;
        Depth = depth;
        Name = node.Name;
        Size = node.Size;
        OwnSize = node.OwnSize;
        FileCount = node.FileCount;
        LastModified = node.LastModified;
        IsFile = node.IsFile;
        foreach (var child in node.Children.Values.OrderByDescending(c => c.Size))
        {
            Children.Add(new DirectoryNodeViewModel(child, totalSize, node.Size, depth + 1));
        }
    }

    public double SubtreePercent => _totalSize == 0 ? 0 : (double)Size / _totalSize;
    public double PercentOfTotal => _totalSize == 0 ? 0 : (double)OwnSize / _totalSize;
    public long TotalFiles => FileCount;
    public int TotalSubdirs => Children.Count;
    public long TotalItems => TotalFiles + TotalSubdirs;
    public string Attributes => string.Empty;

    public double PercentOfParent => _parentSize == 0 ? 1 : (double)Size / _parentSize;

    public string IconGlyph => IsFile ? "\uE8A5" : "\uE8B7"; // Document or Folder

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
