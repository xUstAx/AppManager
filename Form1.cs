using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using AppManager.Models;
using AppManager.Services;

namespace AppManager;

public partial class Form1 : Form
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    private enum CopyFailureAction
    {
        KeepLink,
        Delete,
        Install
    }

    private static readonly System.Text.Json.JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    private static readonly System.Text.Json.JsonSerializerOptions ThemeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppStorageService _storage = new();
    private readonly Dictionary<string, Process> _runningApps = new(StringComparer.OrdinalIgnoreCase);
    private WebView2 _webView = null!;

    public Form1()
    {
        InitializeComponent();              // из Form1.Designer.cs

        Text = "Менеджер приложений";
        Width = 1000;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;

        InitializeWebView();
    }

    private async void InitializeWebView()
    {
        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        await _webView.EnsureCoreWebView2Async();

        // Разрешаем локальные ресурсы (ui/index.html)
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local",
            Path.Combine(AppContext.BaseDirectory, "ui"),
            CoreWebView2HostResourceAccessKind.Allow);

        // Разрешаем доступ к папке с иконками (внутри RootPath)
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "icons.local",
            _storage.RootPath,
            CoreWebView2HostResourceAccessKind.Allow);

        // Приём сообщений из JS
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        _webView.CoreWebView2.Navigate("https://app.local/index.html");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        var action = root.GetProperty("action").GetString();

        switch (action)
        {
            case "getState":
                SendState();
                break;
            case "createCategory":
                {
                    var name = root.GetProperty("name").GetString() ?? "";
                    var created = _storage.CreateCategory(name);
                    PostToJs("categoryCreated", new { name = created });
                    SendState();
                }
                break;
            case "addApp":
                AddAppDialog(root.TryGetProperty("category", out var categoryElement)
                    ? categoryElement.GetString()
                    : null);
                break;
            case "viewCategory":
                OpenCategoryFolder(root.GetProperty("category").GetString() ?? "");
                break;
            case "launchApp":
                {
                    var exe = root.GetProperty("exe").GetString() ?? "";
                    TryLaunch(exe);
                }
                break;
            case "moveApp":
                MoveApp(root);
                break;
            case "deleteApp":
                DeleteApp(root.GetProperty("category").GetString() ?? "",
                    root.GetProperty("folderName").GetString() ?? "");
                break;
            case "setCategoryPinned":
                UpdateCategoryPinned(root);
                break;
            case "saveCategoryOrder":
                UpdateCategoryOrder(root);
                break;
            case "saveTheme":
                SaveTheme(root);
                break;
            case "resetCategoryTheme":
                ResetCategoryTheme(root.GetProperty("category").GetString() ?? "");
                break;
        }
    }

    private static bool IsPathInside(string parent, string path)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative != "." &&
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private void SendState()
    {
        var settings = _storage.GetSettings();
        var categories = _storage.GetOrderedCategories(settings: settings);
        var apps = new Dictionary<string, List<AppEntry>>();
        foreach (var c in categories)
            apps[c] = _storage.GetApps(c);

        var payload = new
        {
            categories,
            apps,
            settings,
            iconRoot = "https://icons.local"
        };
        PostToJs("state", payload);
    }

    private void UpdateCategoryPinned(System.Text.Json.JsonElement root)
    {
        try
        {
            _storage.SetCategoryPinned(
                root.GetProperty("category").GetString() ?? "",
                root.GetProperty("pinned").GetBoolean());
            SendState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось изменить закрепление",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateCategoryOrder(System.Text.Json.JsonElement root)
    {
        try
        {
            var order = root.GetProperty("categories").EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToList();
            _storage.SaveCategoryOrder(order);
            SendState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось изменить порядок каталогов",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveTheme(System.Text.Json.JsonElement root)
    {
        try
        {
            var colors = root.GetProperty("colors").Deserialize<ThemeColors>(ThemeJsonOptions)
                ?? throw new InvalidDataException("Палитра цветов пуста.");
            var category = root.GetProperty("category").GetString();
            if (string.IsNullOrEmpty(category))
                _storage.SaveDefaultTheme(colors);
            else
                _storage.SaveCategoryTheme(category, colors);
            SendState();
            PostToJs("themeSaved", new { category });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось сохранить цвета",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetCategoryTheme(string category)
    {
        try
        {
            _storage.ResetCategoryTheme(category);
            SendState();
            PostToJs("themeSaved", new { category });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось сбросить цвета каталога",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddAppDialog(string? selectedCategory)
    {
        var sourcePath = SelectAppSource(out var sourceIsDirectory);
        if (sourcePath == null) return;

        var sourceExePath = sourceIsDirectory
            ? SelectExecutableFromFolder(sourcePath)
            : sourcePath;
        if (sourceExePath == null) return;

        string? temporaryIconPath = null;
        string? pendingAppDirectory = null;
        var appEntrySaved = false;
        try
        {
            var icon = Icon.ExtractAssociatedIcon(sourceExePath);
            if (icon != null)
            {
                using (icon)
                using (var bitmap = icon.ToBitmap())
                {
                    temporaryIconPath = Path.Combine(Path.GetTempPath(), $"AppManager-{Guid.NewGuid():N}.png");
                    bitmap.Save(temporaryIconPath, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            temporaryIconPath = null;
        }

        try
        {
            var suggestedName = sourceIsDirectory
                ? new DirectoryInfo(sourcePath).Name
                : Path.GetFileNameWithoutExtension(sourcePath);
            using var dlg = new AddAppForm(suggestedName, temporaryIconPath ?? "", selectedCategory);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var folderName = GetUniqueFolderName(dlg.CategoryName, dlg.LatinName);
            var appDirectory = _storage.GetAppDirectory(dlg.CategoryName, folderName);
            pendingAppDirectory = appDirectory;
            var copied = true;
            var copyFailure = "";
            string exePath;

            try
            {
                if (Directory.Exists(appDirectory) || File.Exists(appDirectory))
                    throw new IOException("Папка приложения уже существует.");
                Directory.CreateDirectory(appDirectory);
                if (sourceIsDirectory)
                {
                    if (IsPathInside(sourcePath, appDirectory))
                        throw new IOException("Нельзя копировать папку приложения внутрь самой себя.");
                    CopyDirectory(sourcePath, appDirectory);
                    var relativeExePath = Path.GetRelativePath(sourcePath, sourceExePath);
                    exePath = Path.Combine(appDirectory, relativeExePath);
                }
                else
                {
                    exePath = Path.Combine(appDirectory, Path.GetFileName(sourceExePath));
                    File.Copy(sourceExePath, exePath);
                }
            }
            catch (Exception ex)
            {
                copied = false;
                copyFailure = ex.Message;
                if (Directory.Exists(appDirectory))
                    Directory.Delete(appDirectory, recursive: true);
                Directory.CreateDirectory(appDirectory);
                exePath = sourceExePath;
            }

            var entry = new AppEntry
            {
                Category = dlg.CategoryName,
                FolderName = folderName,
                DisplayName = dlg.DisplayName,
                ExePath = exePath,
                SourcePath = sourcePath,
                SourceIsDirectory = sourceIsDirectory,
                IsLinked = !copied
            };

            if (!string.IsNullOrEmpty(dlg.IconPath) && File.Exists(dlg.IconPath))
            {
                var destIcon = Path.Combine(appDirectory, "icon.png");
                SaveIconAsPng(dlg.IconPath, destIcon);
                entry.IconPath = destIcon;
            }

            _storage.SaveApp(entry);
            appEntrySaved = true;
            SendState();

            if (!copied)
            {
                var action = ShowCopyFailureDialog(entry.DisplayName, copyFailure);
                if (action == CopyFailureAction.Delete)
                {
                    if (DeleteApp(entry.Category, entry.FolderName))
                        OfferInstaller(entry.DisplayName, sourcePath, entry.Category);
                }
                else if (action == CopyFailureAction.Install)
                {
                    OfferInstaller(entry.DisplayName, sourcePath, entry.Category);
                }
            }
        }
        catch (Exception ex)
        {
            if (!appEntrySaved && pendingAppDirectory != null && Directory.Exists(pendingAppDirectory))
                Directory.Delete(pendingAppDirectory, recursive: true);
            MessageBox.Show(ex.Message, "Не удалось добавить приложение",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (temporaryIconPath != null && File.Exists(temporaryIconPath))
            {
                try
                {
                    File.Delete(temporaryIconPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Не удалось удалить временный файл иконки",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }

    private string? SelectAppSource(out bool isDirectory)
    {
        using var choice = new Form
        {
            Text = "Добавить приложение",
            Width = 430,
            Height = 165,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        var hint = new Label
        {
            Text = "Если у программы есть дополнительные файлы, выберите всю папку.",
            AutoSize = true,
            Left = 18,
            Top = 15
        };
        choice.Controls.Add(hint);
        var fileButton = new Button { Text = "Выбрать EXE или ярлык", DialogResult = DialogResult.No };
        fileButton.SetBounds(18, 55, 185, 42);
        var folderButton = new Button { Text = "Выбрать папку приложения", DialogResult = DialogResult.Yes };
        folderButton.SetBounds(215, 55, 185, 42);
        choice.Controls.Add(fileButton);
        choice.Controls.Add(folderButton);

        var result = choice.ShowDialog(this);
        isDirectory = result == DialogResult.Yes;
        if (result == DialogResult.Cancel) return null;

        if (isDirectory)
        {
            using var folderDialog = new FolderBrowserDialog
            {
                Description = "Выберите папку с файлами приложения",
                UseDescriptionForTitle = true
            };
            return folderDialog.ShowDialog(this) == DialogResult.OK ? folderDialog.SelectedPath : null;
        }

        using var fileDialog = new OpenFileDialog
        {
            Title = "Выберите исполняемый файл или ярлык",
            Filter = "Приложения (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|Все файлы (*.*)|*.*"
        };
        return fileDialog.ShowDialog(this) == DialogResult.OK ? fileDialog.FileName : null;
    }

    private string? SelectExecutableFromFolder(string folder)
    {
        var candidates = EnumerateFiles(folder)
            .Where(path => new[] { ".exe", ".bat", ".cmd", ".lnk" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            MessageBox.Show("В выбранной папке не найден EXE, BAT, CMD или ярлык.",
                "Не найдено приложение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        if (candidates.Count == 1) return candidates[0];

        using var dialog = new Form
        {
            Text = "Выберите запускаемый файл",
            Width = 560,
            Height = 420,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable
        };
        var list = new ListBox { Dock = DockStyle.Fill };
        foreach (var candidate in candidates)
            list.Items.Add(Path.GetRelativePath(folder, candidate));
        list.SelectedIndex = 0;
        var ok = new Button { Text = "Выбрать", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 100 };
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        buttons.Controls.Add(ok);
        dialog.Controls.Add(list);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedItem == null) return null;
        return Path.Combine(folder, list.SelectedItem.ToString()!);
    }

    private static IEnumerable<string> EnumerateFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                yield return file;
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(folder))
        {
            if ((File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in EnumerateFiles(subdirectory))
                yield return file;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var target = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            CopyDirectory(directory, target);
        }
    }

    private string GetUniqueFolderName(string category, string requestedName)
    {
        var safeName = AppStorageService.SanitizeName(requestedName);
        var candidate = safeName;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(_storage.RootPath, category, candidate)) ||
               File.Exists(Path.Combine(_storage.RootPath, category, candidate)))
            candidate = $"{safeName} ({suffix++})";
        return candidate;
    }

    private CopyFailureAction ShowCopyFailureDialog(string appName, string error)
    {
        using var dialog = new Form
        {
            Text = "Не удалось скопировать приложение",
            Width = 570,
            Height = 250,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        var message = new Label
        {
            Text = $"«{appName}» добавлено как ссылка на исходные файлы. Возможно, приложение нужно переустановить.\r\n\r\nОшибка копирования: {error}"
        };
        message.SetBounds(15, 15, 525, 95);
        var copyName = new Button { Text = "Скопировать название для поиска" };
        copyName.SetBounds(15, 120, 225, 32);
        copyName.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(appName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Не удалось скопировать название",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        var keep = new Button { Text = "Оставить ссылку", DialogResult = DialogResult.Ignore };
        keep.SetBounds(15, 165, 150, 32);
        var delete = new Button { Text = "Удалить приложение", DialogResult = DialogResult.Abort };
        delete.SetBounds(180, 165, 165, 32);
        var install = new Button { Text = "Выбрать установщик", DialogResult = DialogResult.Retry };
        install.SetBounds(360, 165, 165, 32);
        dialog.Controls.AddRange([message, copyName, keep, delete, install]);
        dialog.CancelButton = keep;
        return dialog.ShowDialog(this) switch
        {
            DialogResult.Abort => CopyFailureAction.Delete,
            DialogResult.Retry => CopyFailureAction.Install,
            _ => CopyFailureAction.KeepLink
        };
    }

    private void OfferInstaller(string appName, string sourcePath, string targetCategory)
    {
        using var dialog = new OpenFileDialog
        {
            Title = $"Выберите установщик для «{appName}»",
            Filter = "Установщики (*.exe;*.msi;*.bat;*.cmd)|*.exe;*.msi;*.bat;*.cmd|Все файлы (*.*)|*.*"
        };
        var initialDirectory = Directory.Exists(sourcePath)
            ? sourcePath
            : Path.GetDirectoryName(sourcePath);
        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            OpenCategoryFolder(targetCategory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось запустить установщик",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void MoveApp(System.Text.Json.JsonElement root)
    {
        try
        {
            _storage.MoveApp(
                root.GetProperty("category").GetString() ?? "",
                root.GetProperty("folderName").GetString() ?? "",
                root.GetProperty("targetCategory").GetString() ?? "");
            SendState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось перенести приложение",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool DeleteApp(string category, string folderName)
    {
        try
        {
            var entry = _storage.GetApp(category, folderName);
            var choice = MessageBox.Show(
                entry.IsLinked
                    ? $"Удалить «{entry.DisplayName}»?\r\n\r\nДа — безвозвратно удалить запись, папку и исходные файлы:\r\n{entry.SourcePath}\r\n\r\nНет — удалить запись, оставив файлы.\r\nОтмена — ничего не менять."
                    : $"Удалить «{entry.DisplayName}»?\r\n\r\nДа — безвозвратно удалить копию приложения из каталога.\r\nНет — удалить только запись, оставив файлы.\r\nОтмена — ничего не менять.",
                "Подтверждение удаления",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);
            if (choice == DialogResult.Cancel) return false;

            if (choice == DialogResult.Yes)
            {
                if (entry.IsLinked)
                {
                    var confirm = MessageBox.Show(
                        $"Это удалит исходные файлы без возможности восстановления:\r\n{entry.SourcePath}\r\n\r\nПродолжить?",
                        "Последнее подтверждение",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Stop);
                    if (confirm != DialogResult.Yes) return false;

                    DeleteSource(entry);
                }
                _storage.DeleteAppDirectory(category, folderName);
            }
            else
            {
                _storage.RemoveAppEntry(category, folderName);
            }

            SendState();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось удалить приложение",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void DeleteSource(AppEntry entry)
    {
        var fullPath = Path.GetFullPath(entry.SourcePath);
        var root = Path.GetPathRoot(fullPath);
        var storageRoot = Path.GetFullPath(_storage.RootPath);
        if (string.IsNullOrEmpty(fullPath) ||
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, storageRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                StringComparison.OrdinalIgnoreCase) ||
            IsPathInside(fullPath, storageRoot) ||
            IsPathInside(storageRoot, fullPath))
            throw new InvalidOperationException("Удаление системного корня или домашней папки запрещено.");

        if (entry.SourceIsDirectory)
        {
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"Исходная папка не найдена: {fullPath}");
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Удаление папки-ссылки запрещено.");
            Directory.Delete(fullPath, recursive: true);
        }
        else
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Исходный файл не найден: {fullPath}");
            File.Delete(fullPath);
        }
    }

    private static void SaveIconAsPng(string sourcePath, string destinationPath)
    {
        if (string.Equals(Path.GetExtension(sourcePath), ".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var icon = new Icon(sourcePath);
            using var bitmap = icon.ToBitmap();
            bitmap.Save(destinationPath, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        using var image = Image.FromFile(sourcePath);
        image.Save(destinationPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void OpenCategoryFolder(string category)
    {
        if (!_storage.GetCategories().Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show("Каталог не найден.", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var path = Path.Combine(_storage.RootPath, category);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось открыть каталог",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TryLaunch(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show($"Файл не найден:\n{path}", "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var appKey = Path.GetFullPath(path);
            if (_runningApps.TryGetValue(appKey, out var runningProcess))
            {
                if (!runningProcess.HasExited)
                {
                    if (runningProcess.MainWindowHandle != IntPtr.Zero)
                        SetForegroundWindow(runningProcess.MainWindowHandle);
                    return;
                }

                runningProcess.Dispose();
                _runningApps.Remove(appKey);
            }

            var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            if (process != null)
                _runningApps[appKey] = process;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось запустить",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PostToJs(string action, object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { action, payload }, WebJsonOptions);
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }
}