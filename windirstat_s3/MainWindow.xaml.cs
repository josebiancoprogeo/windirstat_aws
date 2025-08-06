using System.Windows;
using System.Windows.Controls;
using windirstat_s3.Services;
using windirstat_s3.ViewModels;

namespace windirstat_s3;

public partial class MainWindow : Window
{
    public DirectoryNodeViewModel Root { get; }

    public MainWindow()
    {
        InitializeComponent();

        Root = CreateSampleData();
        DataContext = Root;
    }

    private static DirectoryNodeViewModel CreateSampleData()
    {
        var root = new FolderNode("Root");
        root.Children["Folder1"] = new FolderNode("Folder1") { Size = 300 };
        root.Children["Folder2"] = new FolderNode("Folder2") { Size = 700 };
        return new DirectoryNodeViewModel(root);
    }

    private void DirectoryTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DirectoryNodeViewModel node)
        {
            Treemap.ItemsSource = node.Children;
        }
    }
}
