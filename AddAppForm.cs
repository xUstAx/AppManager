namespace AppManager;

public class AddAppForm : Form
{
    public string DisplayName { get; private set; } = "";
    public string LatinName { get; private set; } = "";
    public string CategoryName { get; private set; } = "";
    public string IconPath { get; private set; } = "";

    private readonly TextBox _display = new();
    private readonly TextBox _latin = new();
    private readonly ComboBox _cat = new();
    private readonly string? _selectedCategory;
    private readonly PictureBox _iconBox = new() { SizeMode = PictureBoxSizeMode.Zoom, Width = 64, Height = 64 };
    private readonly Button _pickIcon = new() { Text = "Выбрать иконку" };

    public AddAppForm(string suggestedLatin, string iconPath, string? selectedCategory = null)
    {
        IconPath = iconPath;
        _selectedCategory = selectedCategory;
        Text = "Добавить приложение";
        Width = 460; Height = 380;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        int y = 15;
        Controls.Add(new Label { Text = "Русское название:", Left = 15, Top = y, Width = 150 });
        _display.SetBounds(170, y, 250, 25);
        _display.Text = suggestedLatin;
        Controls.Add(_display);
        y += 35;

        Controls.Add(new Label { Text = "Имя папки (латиница):", Left = 15, Top = y, Width = 150 });
        _latin.SetBounds(170, y, 250, 25);
        _latin.Text = suggestedLatin;
        Controls.Add(_latin);
        y += 35;

        Controls.Add(new Label { Text = "Каталог:", Left = 15, Top = y, Width = 150 });
        _cat.SetBounds(170, y, 250, 25);
        _cat.DropDownStyle = ComboBoxStyle.DropDownList;
        Controls.Add(_cat);
        y += 35;

        Controls.Add(new Label { Text = "Иконка:", Left = 15, Top = y, Width = 150 });
        _iconBox.SetBounds(170, y, 64, 64);
        Controls.Add(_iconBox);
        _pickIcon.SetBounds(245, y, 130, 30);
        _pickIcon.Click += (_, _) => PickIcon();
        Controls.Add(_pickIcon);
        y += 80;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        ok.SetBounds(250, y, 80, 30);
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(340, y, 80, 30);
        Controls.Add(ok); Controls.Add(cancel);

        AcceptButton = ok; CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_display.Text) || string.IsNullOrWhiteSpace(_latin.Text) ||
                _cat.SelectedItem == null)
            {
                MessageBox.Show("Заполните все поля.");
                DialogResult = DialogResult.None;
                return;
            }
            DisplayName = _display.Text.Trim();
            LatinName = _latin.Text.Trim();
            CategoryName = _cat.SelectedItem!.ToString()!;
        };

        Load += (_, _) =>
        {
            var storage = new AppManager.Services.AppStorageService();
            _cat.Items.AddRange(storage.GetCategories().ToArray());
            var selectedIndex = _selectedCategory == null ? -1 : _cat.Items.IndexOf(_selectedCategory);
            _cat.SelectedIndex = selectedIndex >= 0 ? selectedIndex : (_cat.Items.Count > 0 ? 0 : -1);
            if (!string.IsNullOrEmpty(IconPath) && File.Exists(IconPath))
                _iconBox.ImageLocation = IconPath;
        };
    }

    private void PickIcon()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp;*.ico)|*.png;*.jpg;*.jpeg;*.bmp;*.ico"
        };
        if (ofd.ShowDialog() == DialogResult.OK)
        {
            IconPath = ofd.FileName;
            _iconBox.ImageLocation = IconPath;
        }
    }
}