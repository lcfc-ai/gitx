using System.IO;
using System.Text.Json;
using System.Windows;

namespace GitX.Services;

public static class ThemeManager
{
    public const string VisualStudioDarkKey = "visual-studio-dark";
    public const string VisualStudioLightKey = "visual-studio-light";
    public const string IntelliJDarkKey = "intellij-dark";
    public const string IntelliJLightKey = "intellij-light";

    private static readonly IReadOnlyList<ThemeDefinition> _themes = new[]
    {
        new ThemeDefinition(VisualStudioDarkKey, "Visual Studio Dark", "Themes/VisualStudioDark.xaml"),
        new ThemeDefinition(VisualStudioLightKey, "Visual Studio Light", "Themes/VisualStudioLight.xaml"),
        new ThemeDefinition(IntelliJDarkKey, "IntelliJ Dark", "Themes/IntelliJDark.xaml"),
        new ThemeDefinition(IntelliJLightKey, "IntelliJ Light", "Themes/IntelliJLight.xaml"),
    };

    private static Application? _application;
    private static ResourceDictionary? _activeThemeDictionary;

    private static readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitX",
        "config.json");

    public static IReadOnlyList<ThemeDefinition> Themes => _themes;

    public static string CurrentThemeKey { get; private set; } = VisualStudioDarkKey;

    public static void Initialize(Application application)
    {
        _application = application;
    }

    public static void SaveThemePreference(string key)
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new { theme = key });
            File.WriteAllText(_configPath, json);
        }
        catch
        {
            // 保存失败不影响使用
        }
    }

    public static string? LoadThemePreference()
    {
        try
        {
            if (!File.Exists(_configPath)) return null;
            var json = File.ReadAllText(_configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("theme", out var themeProp))
            {
                return themeProp.GetString();
            }
        }
        catch
        {
            // 读取失败返回 null，使用默认主题
        }
        return null;
    }

    public static void ApplyTheme(string key)
    {
        if (_application == null)
        {
            throw new InvalidOperationException("ThemeManager must be initialized before applying a theme.");
        }

        var theme = _themes.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? _themes[0];

        var assemblyName = typeof(ThemeManager).Assembly.GetName().Name ?? "GitX";
        var themeDictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{assemblyName};component/{theme.ResourcePath}", UriKind.Absolute)
        };

        var merged = _application.Resources.MergedDictionaries;
        if (_activeThemeDictionary != null)
        {
            merged.Remove(_activeThemeDictionary);
        }

        merged.Add(themeDictionary);
        _activeThemeDictionary = themeDictionary;
        CurrentThemeKey = theme.Key;
        SaveThemePreference(theme.Key);
    }
}
