using System.Text.Json.Serialization;

namespace PhoenixToolkit.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PackageChannel>))]
public enum PackageChannel
{
    Develop,
    Release
}
