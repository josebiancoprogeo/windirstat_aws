using windirstat_s3.Services;

namespace windirstat_s3.ViewModels;

public class ExtensionInfoViewModel
{
    public string Extension { get; }
    public long Count { get; }
    public long Size { get; }

    public ExtensionInfoViewModel(string extension, ExtensionInfo info)
    {
        Extension = string.IsNullOrEmpty(extension) ? "(sem extensão)" : extension;
        Count = info.Count;
        Size = info.Size;
    }
}

