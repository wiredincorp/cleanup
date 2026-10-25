namespace Cleanup.ViewModels;

public sealed class FolderRowViewModel
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string SizeText { get; init; }
    public required bool IsFolder { get; init; }
}
