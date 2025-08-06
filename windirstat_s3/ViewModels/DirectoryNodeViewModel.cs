using System.Collections.ObjectModel;
using System.Linq;
using windirstat_s3.Services;

namespace windirstat_s3.ViewModels;

public class DirectoryNodeViewModel
{
    public string Name { get; }
    public long Size { get; }
    public long FileCount { get; }
    public ObservableCollection<DirectoryNodeViewModel> Children { get; } = new();
    public ObservableCollection<ExtensionInfoViewModel> Extensions { get; } = new();

    public string Display => $"{Name} ({FileCount} arquivos, {Size} bytes)";

    public DirectoryNodeViewModel(FolderNode node)
    {
        Name = node.Name;
        Size = node.Size;
        FileCount = node.FileCount;
        foreach (var child in node.Children.Values.OrderByDescending(c => c.Size))
        {
            Children.Add(new DirectoryNodeViewModel(child));
        }
        foreach (var ext in node.Extensions.OrderByDescending(e => e.Value.Size))
        {
            Extensions.Add(new ExtensionInfoViewModel(ext.Key, ext.Value));
        }
    }
}
