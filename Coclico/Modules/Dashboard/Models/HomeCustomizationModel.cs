using System.Text.Json.Serialization;

namespace Coclico.Models;

public enum TileShape
{
    Classic,   // 10px
    Rounded,   // 16px
    Pill,      // 24px
    Square     // 4px
}

public enum TileDensity
{
    Compact,
    Normal,
    Spacious
}

public enum HomeThemeStyle
{
    Classic,
    Glassmorphism,
    Minimalist,
    VibrantNeon
}

public sealed class HomeCustomizationSettings
{
    [JsonPropertyName("shape")]
    public TileShape Shape { get; set; } = TileShape.Rounded;

    [JsonPropertyName("density")]
    public TileDensity Density { get; set; } = TileDensity.Normal;

    [JsonPropertyName("style")]
    public HomeThemeStyle Style { get; set; } = HomeThemeStyle.Classic;

    [JsonPropertyName("gridColumns")]
    public int GridColumns { get; set; } = 3;

    [JsonPropertyName("sectionOrder")]
    public List<string> SectionOrder { get; set; } =
    [
        "Header",
        "Metrics",
        "QuickModes",
        "CoreTiles",
        "CustomShortcuts",
        "RecentActivity"
    ];

    [JsonPropertyName("showHeader")]
    public bool ShowHeader { get; set; } = true;

    [JsonPropertyName("showMetrics")]
    public bool ShowMetrics { get; set; } = true;

    [JsonPropertyName("showQuickModes")]
    public bool ShowQuickModes { get; set; } = true;

    [JsonPropertyName("showCoreTiles")]
    public bool ShowCoreTiles { get; set; } = true;

    [JsonPropertyName("showCustomShortcuts")]
    public bool ShowCustomShortcuts { get; set; } = true;

    [JsonPropertyName("showRecentActivity")]
    public bool ShowRecentActivity { get; set; } = true;

    [JsonPropertyName("showTileRam")]
    public bool ShowTileRam { get; set; } = true;

    [JsonPropertyName("showTileCleaning")]
    public bool ShowTileCleaning { get; set; } = true;

    [JsonPropertyName("showTilePrograms")]
    public bool ShowTilePrograms { get; set; } = true;

    [JsonPropertyName("showTileInstaller")]
    public bool ShowTileInstaller { get; set; } = true;

    [JsonPropertyName("showTileHealth")]
    public bool ShowTileHealth { get; set; } = true;

    [JsonPropertyName("showTileNetwork")]
    public bool ShowTileNetwork { get; set; } = true;
}

