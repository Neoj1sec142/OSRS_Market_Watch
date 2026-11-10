
using MarketAPI.Enums;

namespace MarketAPI.Models;

public sealed class MarketArchiveExport
{
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }

    public ArchiveStatus Status { get; set; }
        = ArchiveStatus.Prepared;

    public string ArchiveFileName { get; set; }
        = string.Empty;

    public int SchemaVersion { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ExportedAtUtc { get; set; }
    public DateTime? TransferredAtUtc { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public DateTime? CommittedAtUtc { get; set; }

    // Set only after verifying the actual archive file.
    public string? ArchiveSha256 { get; set; }

    public long? ArchiveSizeBytes { get; set; }

    // Hash of the canonical export manifest.
    public string? ManifestSha256 { get; set; }

    // Destination confirmed after transfer.
    public string? StorageLocation { get; set; }

    public string? ErrorMessage { get; set; }

    public List<MarketArchiveStreamRange> Ranges { get; set; }
        = new();
}
