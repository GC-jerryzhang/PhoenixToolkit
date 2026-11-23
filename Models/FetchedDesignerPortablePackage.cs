namespace PhoenixToolkit.Models;

internal sealed record FetchedDesignerPortablePackage(
    PackageChannel Channel,
    string? ReleaseVersion,
    string ArchiveVersion,
    string FullPath);
