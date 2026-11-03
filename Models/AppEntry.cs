namespace AppManager.Models;

public class AppEntry
{
    public string FolderName { get; set; } = "";   // имя папки (латиница)
    public string DisplayName { get; set; } = "";  // русская подпись
    public string ExePath { get; set; } = "";      // путь к .exe (или любому файлу)
    public string IconPath { get; set; } = "";     // путь к .png иконке
    public string Category { get; set; } = "";     // имя каталога
    public string SourcePath { get; set; } = "";
    public bool IsLinked { get; set; }
    public bool SourceIsDirectory { get; set; }
}