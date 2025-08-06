using System.Text.Json;
using System.IO;

namespace windirstat_s3.Services;

public static class ReportExporter
{
    public static void ToJson(FolderNode root, string filePath)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(root, options);
        File.WriteAllText(filePath, json);
    }

    public static void ToCsv(FolderNode root, string filePath)
    {
        using var writer = new StreamWriter(filePath);
        writer.WriteLine("Path,Size");
        WriteNode(writer, root, string.Empty);
    }

    private static void WriteNode(StreamWriter writer, FolderNode node, string path)
    {
        var current = string.IsNullOrEmpty(path) ? node.Name : $"{path}/{node.Name}";
        writer.WriteLine($"{current},{node.Size}");
        foreach (var child in node.Children.Values)
        {
            WriteNode(writer, child, current);
        }
    }
}

