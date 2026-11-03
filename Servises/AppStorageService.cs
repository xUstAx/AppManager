using System.Text.Json;
using System.Text.RegularExpressions;
using AppManager.Models;

namespace AppManager.Services;

public class AppStorageService
{
    private readonly string _settingsPath;

    public string RootPath { get; }

    public AppStorageService()
    {
        RootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppManager", "Catalogs");
        Directory.CreateDirectory(RootPath);
        _settingsPath = Path.Combine(Path.GetDirectoryName(RootPath)!, "settings.json");
    }

    /// <summary>Список каталогов (папок) в корне.</summary>
    public List<string> GetCategories()
    {
        return Directory.GetDirectories(RootPath)
                        .Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrEmpty(n) &&
                                    !string.Equals(n, "_temp", StringComparison.OrdinalIgnoreCase))
                        .Cast<string>()
                        .ToList();
    }

    public string CreateCategory(string name)
    {
        var safe = SanitizeName(name);
        var path = Path.Combine(RootPath, safe);
        Directory.CreateDirectory(path);
        return safe;
    }

    public AppManagerSettings GetSettings()
    {
        if (!File.Exists(_settingsPath)) return new AppManagerSettings();
        var json = File.ReadAllText(_settingsPath);
        return JsonSerializer.Deserialize<AppManagerSettings>(json)
            ?? throw new InvalidDataException("Не удалось прочитать настройки приложения.");
    }

    public void SaveDefaultTheme(ThemeColors colors)
    {
        ValidateTheme(colors);
        var settings = GetSettings();
        settings.Defaults = colors;
        SaveSettings(settings);
    }

    public void SaveCategoryTheme(string category, ThemeColors colors)
    {
        ValidateTheme(colors);
        if (!GetCategories().Contains(category, StringComparer.OrdinalIgnoreCase))
            throw new DirectoryNotFoundException($"Каталог «{category}» не найден.");

        var settings = GetSettings();
        settings.CategoryThemes[category] = colors;
        SaveSettings(settings);
    }

    public void ResetCategoryTheme(string category)
    {
        var settings = GetSettings();
        settings.CategoryThemes.Remove(category);
        SaveSettings(settings);
    }

    public void SetCategoryPinned(string category, bool pinned)
    {
        var settings = GetSettings();
        var categories = GetCategories();
        if (!categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            throw new DirectoryNotFoundException($"Каталог «{category}» не найден.");

        var order = GetOrderedCategories(categories, settings);
        var otherPinned = settings.PinnedCategories
            .Where(item => categories.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Where(item => !string.Equals(item, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
        order.RemoveAll(item => string.Equals(item, category, StringComparison.OrdinalIgnoreCase));
        var pinnedCount = order.Count(item => otherPinned.Contains(item, StringComparer.OrdinalIgnoreCase));
        order.Insert(pinnedCount, category);
        settings.PinnedCategories = otherPinned;
        if (pinned) settings.PinnedCategories.Add(category);

        settings.CategoryOrder = order;
        SaveSettings(settings);
    }

    public void SaveCategoryOrder(List<string> requestedOrder)
    {
        var settings = GetSettings();
        var currentCategories = GetCategories();
        if (requestedOrder.Count != currentCategories.Count ||
            requestedOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != currentCategories.Count ||
            currentCategories.Any(category => !requestedOrder.Contains(category, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Порядок каталогов содержит неверный набор элементов.");

        var pinned = new HashSet<string>(settings.PinnedCategories, StringComparer.OrdinalIgnoreCase);
        var requestedPinned = requestedOrder.Where(pinned.Contains).ToList();
        var requestedUnpinned = requestedOrder.Where(category => !pinned.Contains(category)).ToList();
        settings.CategoryOrder = requestedPinned.Concat(requestedUnpinned).ToList();
        SaveSettings(settings);
    }

    public List<string> GetOrderedCategories(List<string>? categories = null, AppManagerSettings? settings = null)
    {
        categories ??= GetCategories();
        settings ??= GetSettings();
        var known = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        var storedOrder = settings.CategoryOrder
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        storedOrder.AddRange(categories.Where(category =>
            !storedOrder.Contains(category, StringComparer.OrdinalIgnoreCase)));

        var pinned = new HashSet<string>(settings.PinnedCategories, StringComparer.OrdinalIgnoreCase);
        return storedOrder.Where(pinned.Contains)
            .Concat(storedOrder.Where(category => !pinned.Contains(category)))
            .ToList();
    }

    /// <summary>Читает все приложения каталога.</summary>
    public List<AppEntry> GetApps(string category)
    {
        var catPath = Path.Combine(RootPath, category);
        if (!Directory.Exists(catPath)) return new List<AppEntry>();

        var result = new List<AppEntry>();
        foreach (var appDir in Directory.GetDirectories(catPath))
        {
            var meta = Path.Combine(appDir, "app.json");
            if (!File.Exists(meta)) continue;
            try
            {
                var json = File.ReadAllText(meta);
                var entry = JsonSerializer.Deserialize<AppEntry>(json);
                if (entry != null) result.Add(entry);
            }
            catch { /* битый json — пропускаем */ }
        }
        return result;
    }

    public void SaveApp(AppEntry entry)
    {
        var appDir = Path.Combine(RootPath, entry.Category, entry.FolderName);
        Directory.CreateDirectory(appDir);
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(appDir, "app.json"), json);
    }

    public string GetAppDirectory(string category, string folderName)
    {
        ValidateName(category);
        ValidateName(folderName);
        if (!GetCategories().Contains(category, StringComparer.OrdinalIgnoreCase))
            throw new DirectoryNotFoundException($"Каталог «{category}» не найден.");

        return Path.Combine(RootPath, category, folderName);
    }

    public AppEntry GetApp(string category, string folderName)
    {
        var app = GetApps(category).FirstOrDefault(candidate =>
            string.Equals(candidate.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        return app ?? throw new FileNotFoundException($"Приложение «{folderName}» не найдено.");
    }

    public void MoveApp(string sourceCategory, string folderName, string targetCategory)
    {
        var app = GetApp(sourceCategory, folderName);
        var sourceDirectory = GetAppDirectory(sourceCategory, folderName);
        var targetDirectory = GetAppDirectory(targetCategory, folderName);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Папка приложения не найдена: {sourceDirectory}");
        if (Directory.Exists(targetDirectory) || File.Exists(targetDirectory))
            throw new IOException($"В каталоге «{targetCategory}» уже есть папка «{folderName}».");

        var oldExePath = app.ExePath;
        var oldIconPath = app.IconPath;
        Directory.Move(sourceDirectory, targetDirectory);
        app.Category = targetCategory;
        app.ExePath = RebasePath(oldExePath, sourceDirectory, targetDirectory);
        app.IconPath = RebasePath(oldIconPath, sourceDirectory, targetDirectory);
        try
        {
            var json = JsonSerializer.Serialize(app, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(targetDirectory, "app.json"), json);
        }
        catch
        {
            app.Category = sourceCategory;
            app.ExePath = oldExePath;
            app.IconPath = oldIconPath;
            Directory.Move(targetDirectory, sourceDirectory);
            throw;
        }
    }

    public void RemoveAppEntry(string category, string folderName)
    {
        var directory = GetAppDirectory(category, folderName);
        var metadata = Path.Combine(directory, "app.json");
        if (!File.Exists(metadata))
            throw new FileNotFoundException($"Запись приложения не найдена: {metadata}");

        File.Delete(metadata);
    }

    public void DeleteAppDirectory(string category, string folderName)
    {
        var directory = GetAppDirectory(category, folderName);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Папка приложения не найдена: {directory}");

        Directory.Delete(directory, recursive: true);
    }

    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "app_" + Guid.NewGuid().ToString("N")[..6] : clean;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
            !string.Equals(name, SanitizeName(name), StringComparison.Ordinal))
            throw new ArgumentException("Недопустимое имя каталога или приложения.");
    }

    private void SaveSettings(AppManagerSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    private static void ValidateTheme(ThemeColors colors)
    {
        var values = new[]
        {
            colors.ToolbarBackground, colors.TabsBackground, colors.AppsBackground,
            colors.CardBackground, colors.ButtonBackground, colors.ButtonHoverBackground,
            colors.DangerButtonBackground, colors.TextColor, colors.MutedTextColor, colors.BorderColor
        };
        if (values.Any(value => !Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$")))
            throw new ArgumentException("Каждый цвет должен быть указан в формате #RRGGBB.");
    }

    private static string RebasePath(string path, string oldDirectory, string newDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var relativePath = Path.GetRelativePath(oldDirectory, Path.GetFullPath(path));
        if (Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            return path;

        return Path.Combine(newDirectory, relativePath);
    }
}