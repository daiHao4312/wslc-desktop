namespace WslcDesktop.Core.Models;

public record ImageModel
{
    public string Id { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public ulong? SizeBytes { get; init; }
    public string CreatedAtRaw { get; init; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; init; }
    public string Digest { get; init; } = string.Empty;

    public string FullName => string.IsNullOrWhiteSpace(Tag) ? Repository : $"{Repository}:{Tag}";
}
