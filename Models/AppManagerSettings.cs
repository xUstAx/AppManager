namespace AppManager.Models;

public class AppManagerSettings
{
    public ThemeColors Defaults { get; set; } = new();
    public Dictionary<string, ThemeColors> CategoryThemes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PinnedCategories { get; set; } = [];
    public List<string> CategoryOrder { get; set; } = [];
}

public class ThemeColors
{
    public string ToolbarBackground { get; set; } = "#2A2A2A";
    public string TabsBackground { get; set; } = "#303030";
    public string AppsBackground { get; set; } = "#1E1E1E";
    public string CardBackground { get; set; } = "#262626";
    public string ButtonBackground { get; set; } = "#3B6EA5";
    public string ButtonHoverBackground { get; set; } = "#4B86C5";
    public string DangerButtonBackground { get; set; } = "#713B3B";
    public string TextColor { get; set; } = "#E8E8E8";
    public string MutedTextColor { get; set; } = "#AAAAAA";
    public string BorderColor { get; set; } = "#454545";
}
