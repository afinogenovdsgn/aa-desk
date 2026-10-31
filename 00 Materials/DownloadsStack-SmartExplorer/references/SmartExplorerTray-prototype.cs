using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal sealed class Shortcut
{
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    internal readonly uint Modifiers;
    internal readonly uint Key;
    internal Shortcut(uint modifiers, uint key) { Modifiers = modifiers; Key = key; }

    internal static Shortcut Parse(string text)
    {
        if (String.IsNullOrWhiteSpace(text)) throw new FormatException("Пустое сочетание.");
        uint modifiers = 0, key = 0;
        string[] parts = text.Split('+');
        foreach (string raw in parts)
        {
            string part = raw.Trim().ToUpperInvariant();
            if (part == "WIN") modifiers |= 0x0008;
            else if (part == "CTRL") modifiers |= 0x0002;
            else if (part == "ALT") modifiers |= 0x0001;
            else if (part == "SHIFT") modifiers |= 0x0004;
            else if (part.Length == 1 && part[0] >= 'A' && part[0] <= 'Z') key = part[0];
            else if (part.Length == 1 && part[0] >= '0' && part[0] <= '9') key = part[0];
            else if (part.Length >= 2 && part[0] == 'F')
            {
                int number;
                if (!Int32.TryParse(part.Substring(1), out number) || number < 1 || number > 24)
                    throw new FormatException("Неизвестная клавиша: " + part);
                key = (uint)(0x70 + number - 1);
            }
            else throw new FormatException("Неизвестная клавиша: " + part);
        }
        if (modifiers == 0 || key == 0 || parts.Length < 2)
            throw new FormatException("Нужно сочетание модификатора и клавиши, например Win+Shift+E.");
        return new Shortcut(modifiers, key);
    }

    internal static string FromKeys(Keys keys)
    {
        Keys key = keys & Keys.KeyCode;
        if (!(key >= Keys.A && key <= Keys.Z) && !(key >= Keys.D0 && key <= Keys.D9) &&
            !(key >= Keys.F1 && key <= Keys.F24)) return null;
        string result = "";
        if ((GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0) result += "Win+";
        if ((keys & Keys.Control) == Keys.Control) result += "Ctrl+";
        if ((keys & Keys.Alt) == Keys.Alt) result += "Alt+";
        if ((keys & Keys.Shift) == Keys.Shift) result += "Shift+";
        if (result.Length == 0) return null;
        if (key >= Keys.D0 && key <= Keys.D9) return result + (char)('0' + ((int)key - (int)Keys.D0));
        return result + key.ToString();
    }
}

internal sealed class CommandRule
{
    internal string Hotkey, Action, Target;
    internal CommandRule(string hotkey, string action, string target)
    { Hotkey = hotkey; Action = action; Target = target; }
}

internal static class Storage
{
    internal static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartExplorer");
    internal static readonly string RulesFile = Path.Combine(Folder, "shortcuts.tsv");
    internal static readonly string LastPathFile = Path.Combine(Folder, "last-path.txt");

    internal static List<CommandRule> Load()
    {
        List<CommandRule> rules = new List<CommandRule>();
        if (File.Exists(RulesFile))
        {
            foreach (string line in File.ReadAllLines(RulesFile, Encoding.UTF8))
            {
                if (String.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                string[] parts = line.Split(new char[] { '\t' }, 3);
                if (parts.Length == 3) rules.Add(new CommandRule(parts[0], parts[1], parts[2]));
            }
        }
        if (rules.Count == 0) rules.Add(new CommandRule("Win+Shift+E", "smart", ""));
        return rules;
    }

    internal static void Save(List<CommandRule> rules)
    {
        Directory.CreateDirectory(Folder);
        List<string> lines = new List<string>();
        lines.Add("# Hotkey<TAB>Action<TAB>Target");
        foreach (CommandRule rule in rules)
            lines.Add(rule.Hotkey + "\t" + rule.Action + "\t" + rule.Target);
        File.WriteAllLines(RulesFile, lines.ToArray(), Encoding.UTF8);
    }

    internal static string ReadLast()
    {
        try { return File.Exists(LastPathFile) ? File.ReadAllText(LastPathFile, Encoding.UTF8) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static void SaveLast(string path)
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(LastPathFile, path, Encoding.UTF8); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class ExplorerActions
{
    internal static string CheckPath(string text)
    {
        if (String.IsNullOrWhiteSpace(text)) return null;
        string path = text.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
            path = path.Substring(1, path.Length - 2);
        bool drive = path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/');
        if (!drive && !path.StartsWith("\\\\", StringComparison.Ordinal)) return null;
        try
        {
            path = Path.GetFullPath(path);
            return Directory.Exists(path) || File.Exists(path) ? path : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }

    private static string ReadClipboard()
    {
        for (int n = 0; n < 3; n++)
        {
            try
            {
                StringCollection files = Clipboard.GetFileDropList();
                if (files != null && files.Count == 1) return files[0];
                if (files != null && files.Count > 1) return null;
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException) { Thread.Sleep(40); }
        }
        return null;
    }

    internal static void Execute(CommandRule rule)
    {
        if (rule.Action == "run")
        {
            if (!File.Exists(rule.Target)) throw new FileNotFoundException("Программа не найдена", rule.Target);
            Process.Start(new ProcessStartInfo(rule.Target) { UseShellExecute = true });
            return;
        }
        string path = null;
        if (rule.Action == "smart")
        {
            string current = CheckPath(ReadClipboard());
            if (current != null) { Storage.SaveLast(current); path = current; }
            else path = CheckPath(Storage.ReadLast());
        }
        else if (rule.Action == "last") path = CheckPath(Storage.ReadLast());
        else if (rule.Action == "open") path = CheckPath(rule.Target);
        Open(rule.Action == "explorer" ? null : path);
    }

    private static void Open(string path)
    {
        string args = path == null ? "" : File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = false });
    }
}

internal sealed class HotkeyWindow : NativeWindow
{
    private const int WM_HOTKEY = 0x0312;
    private readonly Action<int> callback;
    internal HotkeyWindow(Action<int> callback)
    {
        this.callback = callback;
        CreateHandle(new CreateParams());
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WM_HOTKEY) callback(message.WParam.ToInt32());
        base.WndProc(ref message);
    }
}

internal sealed class TrayContext : ApplicationContext
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private readonly NotifyIcon tray;
    private readonly HotkeyWindow window;
    private List<CommandRule> rules;
    private SettingsForm settings;
    private readonly List<int> registered = new List<int>();

    internal TrayContext()
    {
        window = new HotkeyWindow(HandleHotkey);
        tray = new NotifyIcon();
        tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        tray.Text = "SmartExplorer";
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Настроить команды", null, delegate { ShowSettings(); });
        menu.Items.Add("Открыть путь из буфера", null, delegate { Run(new CommandRule("", "smart", "")); });
        menu.Items.Add("Выход", null, delegate { Exit(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { ShowSettings(); };
        tray.Visible = true;
        try
        {
            rules = Storage.Load();
            string error = Apply(rules);
            if (error != null) { ShowSettings(); MessageBox.Show(error, "SmartExplorer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        catch (Exception ex)
        {
            rules = new List<CommandRule> { new CommandRule("Win+Shift+E", "smart", "") };
            ShowSettings();
            MessageBox.Show("Не удалось прочитать настройки: " + ex.Message, "SmartExplorer");
        }
    }

    private string Apply(List<CommandRule> next)
    {
        foreach (int id in registered) UnregisterHotKey(window.Handle, id);
        registered.Clear();
        for (int i = 0; i < next.Count; i++)
        {
            Shortcut shortcut = Shortcut.Parse(next[i].Hotkey);
            // MOD_NOREPEAT prevents repeated Explorer windows while a key is held.
            if (!RegisterHotKey(window.Handle, i + 1, shortcut.Modifiers | 0x4000, shortcut.Key))
            {
                foreach (int id in registered) UnregisterHotKey(window.Handle, id);
                registered.Clear();
                return "Не удалось назначить " + next[i].Hotkey + ". Сочетание может быть занято Windows или другой программой.";
            }
            registered.Add(i + 1);
        }
        return null;
    }

    internal string SaveRules(List<CommandRule> next)
    {
        string error = Apply(next);
        if (error != null) { Apply(rules); return error; }
        try { Storage.Save(next); }
        catch (Exception ex)
        {
            Apply(rules);
            return "Не удалось сохранить настройки: " + ex.Message;
        }
        rules = next;
        return null;
    }

    private void HandleHotkey(int id)
    {
        if (id > 0 && id <= rules.Count) Run(rules[id - 1]);
    }

    private void Run(CommandRule rule)
    {
        try { ExplorerActions.Execute(rule); }
        catch (Exception ex)
        {
            tray.BalloonTipTitle = "SmartExplorer";
            tray.BalloonTipText = ex.Message;
            tray.ShowBalloonTip(4000);
        }
    }

    private void ShowSettings()
    {
        if (settings == null || settings.IsDisposed)
            settings = new SettingsForm(this, rules);
        settings.Show();
        settings.Activate();
    }

    private void Exit()
    {
        foreach (int id in registered) UnregisterHotKey(window.Handle, id);
        registered.Clear();
        tray.Visible = false;
        tray.Dispose();
        window.DestroyHandle();
        ExitThread();
    }
}

internal sealed class SettingsForm : Form
{
    private readonly TrayContext context;
    private readonly DataGridView grid;
    private readonly Label hint;
    private bool capturing;

    internal SettingsForm(TrayContext context, List<CommandRule> rules)
    {
        this.context = context;
        Text = "SmartExplorer — команды";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Size = new Size(760, 420);
        MinimumSize = new Size(620, 330);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        KeyDown += CaptureKey;

        grid = new DataGridView();
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        DataGridViewTextBoxColumn hotkey = new DataGridViewTextBoxColumn();
        hotkey.HeaderText = "Сочетание";
        hotkey.ReadOnly = true;
        hotkey.FillWeight = 28;
        grid.Columns.Add(hotkey);
        DataGridViewComboBoxColumn action = new DataGridViewComboBoxColumn();
        action.HeaderText = "Действие";
        action.FillWeight = 30;
        action.Items.AddRange("Буфер → Проводник", "Последний путь", "Обычный Проводник", "Открыть путь", "Запустить программу");
        grid.Columns.Add(action);
        DataGridViewTextBoxColumn target = new DataGridViewTextBoxColumn();
        target.HeaderText = "Путь (для двух последних действий)";
        target.FillWeight = 55;
        grid.Columns.Add(target);
        foreach (CommandRule rule in rules)
            grid.Rows.Add(rule.Hotkey, DisplayAction(rule.Action), rule.Target);

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = 42;
        buttons.Padding = new Padding(6);
        AddButton(buttons, "Добавить", delegate {
            int row = grid.Rows.Add("", "Буфер → Проводник", "");
            grid.ClearSelection();
            grid.Rows[row].Selected = true;
            grid.CurrentCell = grid.Rows[row].Cells[0];
        });
        AddButton(buttons, "Задать клавиши", delegate { capturing = true; hint.Text = "Нажмите сочетание; Esc — отмена. Для Win используйте клавишу Windows."; });
        AddButton(buttons, "Удалить", delegate { if (grid.CurrentRow != null) grid.Rows.Remove(grid.CurrentRow); });
        AddButton(buttons, "Сохранить", delegate { Save(); });

        hint = new Label();
        hint.Dock = DockStyle.Bottom;
        hint.Height = 42;
        hint.Padding = new Padding(10, 5, 10, 5);
        hint.Text = "Двойной щелчок по значку в трее откроет настройки. После сохранения сочетания действуют сразу.";
        Controls.Add(grid);
        Controls.Add(hint);
        Controls.Add(buttons);
    }

    private static void AddButton(FlowLayoutPanel panel, string text, EventHandler click)
    {
        Button button = new Button();
        button.Text = text;
        button.AutoSize = true;
        button.Click += click;
        panel.Controls.Add(button);
    }

    private void CaptureKey(object sender, KeyEventArgs e)
    {
        if (!capturing) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (e.KeyCode == Keys.Escape) { capturing = false; hint.Text = "Назначение отменено."; return; }
        if (grid.CurrentRow == null) { hint.Text = "Сначала выберите строку."; capturing = false; return; }
        string value = Shortcut.FromKeys(e.KeyData);
        if (value == null) return;
        grid.CurrentRow.Cells[0].Value = value;
        capturing = false;
        hint.Text = "Выбрано " + value + ". Нажмите «Сохранить».";
    }

    private static string DisplayAction(string action)
    {
        if (action == "last") return "Последний путь";
        if (action == "explorer") return "Обычный Проводник";
        if (action == "open") return "Открыть путь";
        if (action == "run") return "Запустить программу";
        return "Буфер → Проводник";
    }

    private static string ActionCode(string action)
    {
        if (action == "Последний путь") return "last";
        if (action == "Обычный Проводник") return "explorer";
        if (action == "Открыть путь") return "open";
        if (action == "Запустить программу") return "run";
        return "smart";
    }

    private void Save()
    {
        grid.EndEdit();
        List<CommandRule> next = new List<CommandRule>();
        HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                string hotkey = Convert.ToString(row.Cells[0].Value).Trim();
                string action = ActionCode(Convert.ToString(row.Cells[1].Value));
                string target = Convert.ToString(row.Cells[2].Value).Trim();
                Shortcut.Parse(hotkey);
                if (!used.Add(hotkey)) throw new FormatException("Сочетание повторяется: " + hotkey);
                if ((action == "open" || action == "run") && target.Length == 0)
                    throw new FormatException("Для действия «" + DisplayAction(action) + "» укажите путь.");
                if (target.Contains("\t") || target.Contains("\n") || target.Contains("\r"))
                    throw new FormatException("Путь не должен содержать переносы строк или табуляцию.");
                next.Add(new CommandRule(hotkey, action, target));
            }
            if (next.Count == 0) throw new FormatException("Добавьте хотя бы одну команду.");
            string error = context.SaveRules(next);
            if (error != null) throw new Exception(error);
            hint.Text = "Сохранено. Сочетания действуют сразу.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "SmartExplorer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}

internal static class TrayProgram
{
    [STAThread]
    private static void Main()
    {
        bool created;
        using (Mutex mutex = new Mutex(true, "Local\\SmartExplorerStandalone", out created))
        {
            if (!created) { MessageBox.Show("SmartExplorer уже запущен. Его значок находится в трее."); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }
}
