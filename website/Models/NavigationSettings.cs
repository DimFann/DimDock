using System.Text.Json.Serialization;

namespace Website.Models;

public sealed class NavigationSettings
{
    [JsonRequired]
    public bool Gallery { get; set; } = true;

    [JsonRequired]
    public bool Search { get; set; } = true;

    [JsonRequired]
    public bool Blog { get; set; } = true;

    public bool Commissions { get; set; } = true;
}
