using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Amazon.S3;
using Amazon.S3.Model;

namespace windirstat_s3;

public partial class S3PrefixPickerWindow : Window, INotifyPropertyChanged
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucketName;
    private string _selectedPrefix = string.Empty;

    public string BucketName => $"Bucket: {_bucketName}";

    public string SelectedPrefix
    {
        get => _selectedPrefix;
        private set
        {
            _selectedPrefix = value;
            OnPropertyChanged(nameof(SelectedPrefix));
            OnPropertyChanged(nameof(SelectedPrefixDisplay));
        }
    }

    public string SelectedPrefixDisplay =>
        string.IsNullOrWhiteSpace(SelectedPrefix) ? "Prefixo: (raiz)" : $"Prefixo: {SelectedPrefix}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public S3PrefixPickerWindow(IAmazonS3 s3, string bucketName)
    {
        _s3 = s3;
        _bucketName = bucketName;
        InitializeComponent();
        DataContext = this;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (PrefixTree.Items.Count == 0)
        {
            await LoadRootAsync();
        }
    }

    private Task LoadRootAsync()
    {
        PrefixTree.Items.Clear();
        var rootItem = new TreeViewItem
        {
            Header = "(raiz)",
            Tag = string.Empty
        };
        rootItem.Expanded += TreeItem_Expanded;
        rootItem.Items.Add(new TreeViewItem());
        PrefixTree.Items.Add(rootItem);
        rootItem.IsExpanded = true;
        PrefixTree.SelectedItemChanged -= PrefixTree_SelectedItemChanged;
        PrefixTree.SelectedItemChanged += PrefixTree_SelectedItemChanged;
        return Task.CompletedTask;
    }

    private async void TreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item)
        {
            return;
        }

        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem)
        {
            return;
        }

        item.Items.Clear();
        var prefix = item.Tag as string ?? string.Empty;

        var request = new ListObjectsV2Request
        {
            BucketName = _bucketName,
            Prefix = prefix,
            Delimiter = "/"
        };

        var response = await _s3.ListObjectsV2Async(request);
        foreach (var childPrefix in response.CommonPrefixes.OrderBy(p => p))
        {
            var name = childPrefix.EndsWith("/")
                ? childPrefix[(prefix.Length)..].TrimEnd('/')
                : childPrefix[(prefix.Length)..];

            var child = new TreeViewItem
            {
                Header = name,
                Tag = childPrefix
            };
            child.Expanded += TreeItem_Expanded;
            child.Items.Add(new TreeViewItem());
            item.Items.Add(child);
        }
    }

    private void PrefixTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item)
        {
            SelectedPrefix = item.Tag as string ?? string.Empty;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
