namespace PhoenixToolkit.Models;

internal sealed record PackageSource(PackageChannel Channel, string Directory, string? ReleaseVersion = null)
{
    public string Label => Channel == PackageChannel.Release ? $"release/{ReleaseVersion}" : "develop";
}
