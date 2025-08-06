using System.Collections.ObjectModel;
using System.Linq;
using windirstat_s3.Services;

namespace windirstat_s3.ViewModels;

public class DirectoryNodeViewModel
{
    public string Name { get; }
    public long Size { get; }
    public ObservableCollection<DirectoryNodeViewModel> Children { get; }

    public DirectoryNodeViewModel(FolderNode node)
    {
        Name = node.Name;
        Size = node.Size;
        Children = new ObservableCollection<DirectoryNodeViewModel>(
            node.Children.Values.Select(child => new DirectoryNodeViewModel(child)));
    }
}
