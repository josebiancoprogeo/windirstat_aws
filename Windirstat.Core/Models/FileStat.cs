using System;

namespace Windirstat.Core.Models;

public class FileStat
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
}
