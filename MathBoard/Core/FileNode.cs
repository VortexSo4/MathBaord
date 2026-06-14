namespace MathBoard.Core;

public class FileNode(string name, string fullPath, bool isDirectory)
{
    public string Name { get; } = name;
    public string FullPath { get; } = fullPath;
    public bool IsDirectory { get; } = isDirectory;
    public List<FileNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;
}