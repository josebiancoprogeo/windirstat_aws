using System;
using System.Collections.Generic;

namespace Windirstat.Core.Models;

public class FolderNode
{
    public string Name { get; }
    public long Size { get; set; }
    public long FileCount { get; set; }
    public long OwnSize { get; set; }
    public DateTime LastModified { get; set; }
    public Dictionary<string, FolderNode> Children { get; } = new();
    public Dictionary<string, ExtensionInfo> Extensions { get; } = new();

    public FolderNode(string name)
    {
        Name = name;
    }
}
