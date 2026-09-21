using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.SqlClient;

namespace MdfBakViewer;

public sealed class MainForm : Form
{
    private readonly ToolStripComboBox _cmbInstance;
    private readonly ToolStripButton _btnAutoLocalDb;
    private readonly ToolStripButton _btnConnect;
    private readonly ToolStripButton _btnOpen;
    private readonly ToolStripButton _btnRefresh;
    private readonly ToolStripButton _btnDetach;

    private readonly Label _dropHint;
    private readonly TreeView _tree;
    private readonly TabControl _tabs;
    private readonly TabPage _tabData;
    private readonly DataGridView _dgvColumns;
    private readonly DataGridView _dgvData;
    private readonly ToolStripStatusLabel _status;
    private readonly ContextMenuStrip _treeMenu;

    private bool _busy;
    private string? _lastFolder;

    private static string WorkDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MdfBakViewer");

    public MainForm()
    {
        Text = "MDF/BAK Viewer — LocalDB Business Edition";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1360, 860);
        Font = new Font("Segoe UI", 9F);

        var toolStrip = new ToolStrip
        {
            Dock = DockStyle.Fill,
            GripStyle = ToolStripGripStyle.Hidden
        };

        _cmbInstance = new ToolStripComboBox
        {
            Width = 300,
            DropDownStyle = ComboBoxStyle.DropDown,
            ToolTipText = "SQL Server instance. Обычно подходит (localdb)\\MSSQLLocalDB"
        };

        _cmbInstance.Items.AddRange(new object[]
        {
            @"(localdb)\MSSQLLocalDB",
            @"(localdb)\v11.0",
            @"(localdb)\v12.0",
            @"(localdb)\v13.0",
            @"(localdb)\v14.0",
            @"(localdb)\v15.0",
            @"(localdb)\v16.0",
            @".\SQLEXPRESS",
            @"localhost\SQLEXPRESS"
        });

        _cmbInstance.Text = @"(localdb)\MSSQLLocalDB";

        _btnAutoLocalDb = new ToolStripButton("Авто LocalDB")
        {
            ToolTipText = "Найти/создать/запустить LocalDB автоматически"
        };

        _btnConnect = new ToolStripButton("Проверить подключение");
        _btnOpen = new ToolStripButton("Выбрать файлы...");
        _btnRefresh = new ToolStripButton("Обновить базы");
        _btnDetach = new ToolStripButton("Отключить базу");

        toolStrip.Items.Add(new ToolStripLabel("SQL Server:"));
        toolStrip.Items.Add(_cmbInstance);
        toolStrip.Items.Add(_btnAutoLocalDb);
        toolStrip.Items.Add(_btnConnect);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_btnOpen);
        toolStrip.Items.Add(_btnRefresh);
        toolStrip.Items.Add(_btnDetach);

        _btnAutoLocalDb.Click += async (s, e) => await AutoLocalDbAsync();
        _btnConnect.Click += async (s, e) => await CheckConnectionAsync();
        _btnOpen.Click += async (s, e) => await OpenFileAsync();
        _btnRefresh.Click += async (s, e) => await RefreshDatabasesAsync();
        _btnDetach.Click += async (s, e) => await DetachSelectedDatabaseAsync();

        _dropHint = new Label
        {
            Text = "Перетащите .mdf / .bak сюда или нажмите, чтобы открыть проводник",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 56,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(243, 246, 251),
            ForeColor = Color.FromArgb(31, 55, 95),
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand,
            Padding = new Padding(8)
        };

        _dropHint.Click += async (s, e) => await OpenFileAsync();

        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            ShowNodeToolTips = true
        };

        _tree.AfterSelect += async (s, e) =>
        {
            if (e.Node is not null)
                await TreeAfterSelectAsync(e.Node);
        };

        _tree.BeforeExpand += async (s, e) =>
        {
            if (e.Node is not null)
                await TreeBeforeExpandAsync(e.Node);
        };

        _tree.NodeMouseDoubleClick += async (s, e) =>
        {
            if (e.Node is not null)
                await TreeDoubleClickAsync(e.Node);
        };

        var leftPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1
        };

        leftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        leftPanel.Controls.Add(_dropHint, 0, 0);
        leftPanel.Controls.Add(_tree, 0, 1);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 420
        };

        split.Panel1.Controls.Add(leftPanel);

        _dgvColumns = CreateGrid();
        _dgvData = CreateGrid();

        _tabs = new TabControl { Dock = DockStyle.Fill };

        var tabColumns = new TabPage("Структура таблицы");
        _tabData = new TabPage("Данные (топ 100)");

        tabColumns.Controls.Add(_dgvColumns);
        _tabData.Controls.Add(_dgvData);

        _tabs.TabPages.Add(tabColumns);
        _tabs.TabPages.Add(_tabData);

        split.Panel2.Controls.Add(_tabs);

        _status = new ToolStripStatusLabel("Запуск...")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(toolStrip, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(statusStrip, 0, 2);

        Controls.Add(root);

        _treeMenu = new ContextMenuStrip();

        _treeMenu.Items.Add(
            "Обновить список баз",
            null,
            async (s, e) => await RefreshDatabasesAsync());

        _treeMenu.Items.Add(
            "Показать данные (топ 100)",
            null,
            async (s, e) =>
            {
                if (_tree.SelectedNode?.Tag is TableInfo info)
                    await LoadDataAsync(info);
            });

        _treeMenu.Items.Add(
            "Отключить выбранную базу",
            null,
            async (s, e) => await DetachSelectedDatabaseAsync());

        _tree.ContextMenuStrip = _treeMenu;

        AllowDrop = true;
        DragEnter += MainForm_DragEnter;
        DragDrop += MainForm_DragDrop;

        Shown += async (s, e) => await AutoSetupAsync();
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
        RowHeadersVisible = false,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
    };

    private void MainForm_DragEnter(object? sender, DragEventArgs e)
    {
        bool hasFiles = e.Data is not null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effect = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void MainForm_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            await ProcessFilesAsync(files);
    }

    private async Task OpenFileAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "SQL Server файлы|*.mdf;*.bak|MDF|*.mdf|BAK|*.bak|Все файлы|*.*",
            Multiselect = true,
            Title = "Выберите .mdf или .bak"
        };

        if (!string.IsNullOrWhiteSpace(_lastFolder) && Directory.Exists(_lastFolder))
            dlg.InitialDirectory = _lastFolder;

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var first = dlg.FileNames.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
                _lastFolder = Path.GetDirectoryName(first);

            await ProcessFilesAsync(dlg.FileNames);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        _btnAutoLocalDb.Enabled = !busy;
        _btnConnect.Enabled = !busy;
        _btnOpen.Enabled = !busy;
        _btnRefresh.Enabled = !busy;
        _btnDetach.Enabled = !busy;

        UseWaitCursor = busy;
    }

    private async Task AutoSetupAsync()
    {
        SetBusy(true);

        bool connected = false;

        try
        {
            ShowStatus("Автопоиск SQL Server LocalDB...");

            var found = await DetectWorkingInstanceAsync();

            if (found is not null)
            {
                _cmbInstance.Text = found;
                connected = true;
                ShowStatus($"Найден сервер: {found}");
            }
            else
            {
                ShowStatus("SQL Server LocalDB не найден.");

                var result = MessageBox.Show(
                    this,
                    "Не найден SQL Server LocalDB.\n\n" +
                    "Для просмотра .mdf/.bak нужен движок SQL Server.\n" +
                    "Рекомендую один раз установить 'SQL Server Express LocalDB'.\n\n" +
                    "Открыть страницу установки/документации?",
                    "Нужен SQL Server LocalDB",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);

                if (result == DialogResult.OK)
                    OpenDownloadPage();
            }
        }
        finally
        {
            SetBusy(false);
        }

        if (connected)
            await RefreshDatabasesAsync();
    }

    private async Task AutoLocalDbAsync()
    {
        SetBusy(true);

        bool connected = false;

        try
        {
            ShowStatus("Автонастройка LocalDB...");

            var found = await DetectWorkingInstanceAsync();

            if (found is not null)
            {
                _cmbInstance.Text = found;
                connected = true;
                ShowStatus($"LocalDB готов: {found}");
            }
            else
            {
                ShowStatus("LocalDB не найден.");

                MessageBox.Show(
                    this,
                    "Не удалось найти/запустить LocalDB.\n\n" +
                    "Установи 'SQL Server Express LocalDB' и нажми 'Авто LocalDB' еще раз.",
                    "LocalDB",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            SetBusy(false);
        }

        if (connected)
            await RefreshDatabasesAsync();
    }

    private async Task<string?> DetectWorkingInstanceAsync()
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(_cmbInstance.Text))
            candidates.Add(_cmbInstance.Text.Trim());

        candidates.AddRange(new[]
        {
            @"(localdb)\MSSQLLocalDB",
            @"(localdb)\v11.0",
            @"(localdb)\v12.0",
            @"(localdb)\v13.0",
            @"(localdb)\v14.0",
            @"(localdb)\v15.0",
            @"(localdb)\v16.0",
            @".\SQLEXPRESS",
            @"localhost\SQLEXPRESS"
        });

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (await TryConnectAsync(candidate))
                return candidate;
        }

        // Попытка оживить дефолтный LocalDB через sqllocaldb.exe
        await Task.Run(() => TrySqlLocalDb("start MSSQLLocalDB"));

        if (await TryConnectAsync(@"(localdb)\MSSQLLocalDB"))
            return @"(localdb)\MSSQLLocalDB";

        await Task.Run(() => TrySqlLocalDb("create MSSQLLocalDB"));
        await Task.Run(() => TrySqlLocalDb("start MSSQLLocalDB"));

        if (await TryConnectAsync(@"(localdb)\MSSQLLocalDB"))
            return @"(localdb)\MSSQLLocalDB";

        return null;
    }

    private static async Task<bool> TryConnectAsync(string dataSource)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = dataSource,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                ConnectTimeout = 4,
                MultipleActiveResultSets = true
            };

            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySqlLocalDb(string arguments)
    {
        foreach (var exe in SqlLocalDbExecutableCandidates())
        {
            int exitCode = RunProcess(exe, arguments);
            if (exitCode == 0)
                return true;
        }

        return false;
    }

    private static IEnumerable<string> SqlLocalDbExecutableCandidates()
    {
        yield return "sqllocaldb.exe";

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var version in new[] { "160", "150", "140", "130", "120", "110" })
        {
            yield return Path.Combine(
                programFiles,
                $@"Microsoft SQL Server\{version}\Tools\Binn\SqlLocalDB.exe");
        }
    }

    private static int RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);

            if (process is null)
                return -1;

            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // ignore
                }

                return -1;
            }

            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "https://learn.microsoft.com/ru-ru/sql/database-engine/configure-windows/sql-server-express-localdb")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    private async Task CheckConnectionAsync()
    {
        try
        {
            SetBusy(true);
            ShowStatus("Проверка подключения...");

            await using var conn = CreateConnection();
            await conn.OpenAsync();

            using var cmd = new SqlCommand("SELECT @@VERSION;", conn);
            var version = await cmd.ExecuteScalarAsync() as string;

            ShowStatus("Подключено: " + (version?.Split('\n')[0] ?? "SQL Server"));
        }
        catch (Exception ex)
        {
            ShowStatus("Ошибка подключения.");
            ShowError(
                "Не удалось подключиться к SQL Server. Проверь инстанс или установи SQL Server Express/LocalDB.",
                ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ProcessFilesAsync(string[] files)
    {
        if (_busy)
        {
            ShowStatus("Уже выполняется операция. Дождитесь завершения.");
            return;
        }

        var supported = files
            .Where(f => f.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (supported.Length == 0)
        {
            MessageBox.Show(
                this,
                "Поддерживаются только файлы .mdf и .bak.",
                "Файлы",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        SetBusy(true);

        var loaded = new List<string>();

        try
        {
            foreach (var file in supported)
            {
                string dbName = file.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase)
                    ? await AttachMdfAsync(file)
                    : await RestoreBakAsync(file);

                loaded.Add(dbName);
            }
        }
        catch (Exception ex)
        {
            ShowError("Ошибка обработки файла", ex);
        }
        finally
        {
            SetBusy(false);
        }

        if (loaded.Count > 0)
        {
            await RefreshDatabasesAsync(loaded[^1]);
            ShowStatus($"Готово. Загружено баз: {loaded.Count}. Последняя: {loaded[^1]}.");
        }
    }

    private async Task RefreshDatabasesAsync(string? selectDatabase = null)
    {
        try
        {
            SetBusy(true);
            ShowStatus("Обновление списка баз...");

            await using var conn = CreateConnection();
            await conn.OpenAsync();

            var dt = await ExecuteQueryAsync(
                conn,
                "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name;");

            _tree.BeginUpdate();
            _tree.Nodes.Clear();

            TreeNode? target = null;

            foreach (DataRow row in dt.Rows)
            {
                var name = row["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var node = new TreeNode(name)
                {
                    Tag = name,
                    ToolTipText = name
                };

                node.Nodes.Add(new TreeNode("Загрузка..."));
                _tree.Nodes.Add(node);

                if (string.Equals(name, selectDatabase, StringComparison.OrdinalIgnoreCase))
                    target = node;
            }

            _tree.EndUpdate();

            if (target is not null)
            {
                await LoadTablesAsync(target);
                target.Expand();
                _tree.SelectedNode = target;
            }

            ShowStatus($"Баз данных: {dt.Rows.Count}.");
        }
        catch (Exception ex)
        {
            ShowError("Не удалось получить список баз", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadTablesAsync(TreeNode dbNode)
    {
        if (dbNode.Tag is not string db)
            return;

        try
        {
            ShowStatus($"Чтение таблиц базы {db}...");

            await using var conn = CreateConnection();
            await conn.OpenAsync();

            string sql = $@"
SELECT s.name AS SchemaName, t.name AS TableName
FROM {Quote(db)}.sys.tables t
JOIN {Quote(db)}.sys.schemas s ON t.schema_id = s.schema_id
WHERE t.is_ms_shipped = 0
ORDER BY s.name, t.name;";

            var dt = await ExecuteQueryAsync(conn, sql);

            dbNode.Nodes.Clear();

            if (dt.Rows.Count == 0)
            {
                dbNode.Nodes.Add(new TreeNode("(нет пользовательских таблиц)"));
                return;
            }

            foreach (DataRow row in dt.Rows)
            {
                string schema = row["SchemaName"]?.ToString() ?? "dbo";
                string table = row["TableName"]?.ToString() ?? "???";

                var tableNode = new TreeNode($"{schema}.{table}")
                {
                    Tag = new TableInfo(db, schema, table),
                    ToolTipText = $"{db}.{schema}.{table}"
                };

                dbNode.Nodes.Add(tableNode);
            }

            ShowStatus($"Таблиц в базе {db}: {dt.Rows.Count}.");
        }
        catch (Exception ex)
        {
            dbNode.Nodes.Clear();
            dbNode.Nodes.Add(new TreeNode($"Ошибка: {ex.Message}"));
        }
    }

    private async Task TreeBeforeExpandAsync(TreeNode node)
    {
        if (node.Tag is string &&
            node.Nodes.Count == 1 &&
            node.Nodes[0].Text == "Загрузка...")
        {
            await LoadTablesAsync(node);
        }
    }

    private async Task TreeAfterSelectAsync(TreeNode node)
    {
        _dgvData.DataSource = null;

        if (node.Tag is TableInfo info)
        {
            await LoadColumnsAsync(info);
            _tabs.SelectedIndex = 0;
        }
        else if (node.Tag is string &&
                 node.Nodes.Count == 1 &&
                 node.Nodes[0].Text == "Загрузка...")
        {
            await LoadTablesAsync(node);
            node.Expand();
        }
    }

    private async Task TreeDoubleClickAsync(TreeNode node)
    {
        if (node.Tag is TableInfo info)
            await LoadDataAsync(info);
    }

    private async Task LoadColumnsAsync(TableInfo info)
    {
        try
        {
            ShowStatus($"Чтение структуры {info.Schema}.{info.Table}...");

            await using var conn = CreateConnection();
            await conn.OpenAsync();

            string sql = $@"
SELECT
    COLUMN_NAME AS [Столбец],
    DATA_TYPE AS [Тип],
    CHARACTER_MAXIMUM_LENGTH AS [Длина],
    NUMERIC_PRECISION AS [Точность],
    NUMERIC_SCALE AS [Масштаб],
    IS_NULLABLE AS [NULL],
    COLUMN_DEFAULT AS [Значение по умолчанию],
    ORDINAL_POSITION AS [Порядок]
FROM {Quote(info.Database)}.INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION;";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@schema", info.Schema);
            cmd.Parameters.AddWithValue("@table", info.Table);

            var dt = await ExecuteQueryAsync(cmd);

            _dgvColumns.DataSource = dt;
            ShowStatus($"Колонок в таблице {info.Schema}.{info.Table}: {dt.Rows.Count}.");
        }
        catch (Exception ex)
        {
            ShowError("Не удалось получить структуру таблицы", ex);
        }
    }

    private async Task LoadDataAsync(TableInfo info)
    {
        try
        {
            SetBusy(true);
            ShowStatus($"Чтение данных {info.Schema}.{info.Table}...");

            await using var conn = CreateConnection();
            await conn.OpenAsync();

            string sql = $@"
SELECT TOP 100 *
FROM {Quote(info.Database)}.{Quote(info.Schema)}.{Quote(info.Table)};";

            var dt = await ExecuteQueryAsync(conn, sql, 120);

            _dgvData.DataSource = dt;
            _tabs.SelectedIndex = 1;

            ShowStatus($"Показаны первые {dt.Rows.Count} строк из {info.Schema}.{info.Table}.");
        }
        catch (Exception ex)
        {
            ShowError("Не удалось получить данные таблицы", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DetachSelectedDatabaseAsync()
    {
        string? db = GetSelectedDatabaseName();

        if (string.IsNullOrWhiteSpace(db))
        {
            MessageBox.Show(
                this,
                "Выберите базу в дереве слева.",
                "База",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        if (new[] { "master", "model", "msdb", "tempdb" }
            .Contains(db, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "Системные базы отключать нельзя.",
                "База",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        var result = MessageBox.Show(
            this,
            $"Отключить базу {db}?\n\nФайлы базы останутся в рабочей папке приложения.",
            "Отключение базы",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
            return;

        try
        {
            SetBusy(true);
            ShowStatus($"Отключение базы {db}...");

            await using var conn = CreateConnection();
            await conn.OpenAsync();

            string sql = $@"
ALTER DATABASE {Quote(db)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
EXEC sp_detach_db @dbname = @db;";

            using var cmd = new SqlCommand(sql, conn)
            {
                CommandTimeout = 0
            };

            cmd.Parameters.AddWithValue("@db", db);

            await cmd.ExecuteNonQueryAsync();

            await RefreshDatabasesAsync();

            ShowStatus($"База {db} отключена.");
        }
        catch (Exception ex)
        {
            ShowError("Не удалось отключить базу", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string? GetSelectedDatabaseName()
    {
        var node = _tree.SelectedNode;

        while (node is not null)
        {
            if (node.Tag is string db)
                return db;

            if (node.Tag is TableInfo info)
                return info.Database;

            node = node.Parent;
        }

        return null;
    }

    private async Task<string> AttachMdfAsync(string mdfPath)
    {
        Directory.CreateDirectory(WorkDir);

        ShowStatus($"Копирование {Path.GetFileName(mdfPath)}...");

        string workMdf = await CopyToWorkAsync(mdfPath);

        string? originalLog = FindLogFile(mdfPath);
        string? workLog = originalLog is null
            ? null
            : await CopyToWorkAsync(originalLog);

        string dbName = MakeDbName(Path.GetFileNameWithoutExtension(mdfPath));

        await using var conn = CreateConnection();
        await conn.OpenAsync();

        string attachSql = workLog is null
            ? $"CREATE DATABASE {Quote(dbName)} ON (FILENAME=N'{Escape(workMdf)}') FOR ATTACH;"
            : $"CREATE DATABASE {Quote(dbName)} ON (FILENAME=N'{Escape(workMdf)}'), (FILENAME=N'{Escape(workLog)}') FOR ATTACH;";

        try
        {
            ShowStatus($"Подключение MDF как {dbName}...");
            await ExecuteNonQueryAsync(conn, attachSql, 0);
        }
        catch (SqlException ex)
        {
            ShowStatus($"Attach не удался (код {ex.Number}). Пробуем пересоздать лог...");

            string rebuildSql = $@"
CREATE DATABASE {Quote(dbName)}
ON (FILENAME=N'{Escape(workMdf)}')
FOR ATTACH_REBUILD_LOG;";

            await ExecuteNonQueryAsync(conn, rebuildSql, 0);
        }

        return dbName;
    }

    private async Task<string> RestoreBakAsync(string bakPath)
    {
        Directory.CreateDirectory(WorkDir);

        ShowStatus($"Чтение заголовка {Path.GetFileName(bakPath)}...");

        await using var conn = CreateConnection();
        await conn.OpenAsync();

        string headerSql = $"RESTORE HEADERONLY FROM DISK=N'{Escape(bakPath)}'";
        DataTable header = await ExecuteQueryAsync(conn, headerSql, 0);

        string? backupDbName = header.Rows.Count > 0
            ? header.Rows[0]["DatabaseName"]?.ToString()
            : null;

        string dbName = MakeDbName(string.IsNullOrWhiteSpace(backupDbName)
            ? Path.GetFileNameWithoutExtension(bakPath)
            : backupDbName!);

        string fileListSql = $"RESTORE FILELISTONLY FROM DISK=N'{Escape(bakPath)}'";
        DataTable files = await ExecuteQueryAsync(conn, fileListSql, 0);

        if (files.Rows.Count == 0)
            throw new InvalidOperationException("Не удалось получить список файлов из резервной копии.");

        var moves = new List<string>();
        int dataFileIndex = 0;

        foreach (DataRow row in files.Rows)
        {
            string logical = row["LogicalName"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(logical))
                continue;

            string type = row["Type"]?.ToString()?.Trim().ToUpperInvariant() ?? "D";

            string ext = type switch
            {
                "D" => dataFileIndex++ == 0 ? ".mdf" : ".ndf",
                "L" => ".ldf",
                "F" => ".ft",
                _ => ".ndf"
            };

            string physical = Path.Combine(
                WorkDir,
                $"{Sanitize(dbName, 40)}_{Sanitize(logical, 40)}_{Guid.NewGuid():N}{ext}");

            moves.Add($"MOVE N'{Escape(logical)}' TO N'{Escape(physical)}'");
        }

        if (moves.Count == 0)
            throw new InvalidOperationException("Не удалось построить список файлов для RESTORE.");

        string restoreSql = $@"
RESTORE DATABASE {Quote(dbName)}
FROM DISK=N'{Escape(bakPath)}'
WITH FILE = 1, {string.Join(", ", moves)}, REPLACE;";

        ShowStatus($"Восстановление базы {dbName}. Это может занять время...");

        await ExecuteNonQueryAsync(conn, restoreSql, 0);

        return dbName;
    }

    private static async Task<string> CopyToWorkAsync(string source)
    {
        Directory.CreateDirectory(WorkDir);

        string fileName = Path.GetFileName(source);
        string ext = Path.GetExtension(fileName);
        string baseName = Path.GetFileNameWithoutExtension(fileName);

        string dest = Path.Combine(
            WorkDir,
            $"{Sanitize(baseName, 60)}_{Guid.NewGuid():N}{ext}");

        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true);

        await using var output = new FileStream(
            dest,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            useAsync: true);

        await input.CopyToAsync(output);

        return dest;
    }

    private static string? FindLogFile(string mdfPath)
    {
        string? dir = Path.GetDirectoryName(mdfPath);
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        string baseName = Path.GetFileNameWithoutExtension(mdfPath);

        string[] candidates =
        {
            Path.ChangeExtension(mdfPath, ".ldf"),
            Path.Combine(dir, baseName + "_log.ldf"),
            Path.Combine(dir, baseName + ".log"),
            Path.ChangeExtension(mdfPath, ".log")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private SqlConnection CreateConnection(string initialCatalog = "master")
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(_cmbInstance.Text)
                ? @"(localdb)\MSSQLLocalDB"
                : _cmbInstance.Text.Trim(),

            InitialCatalog = initialCatalog,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            MultipleActiveResultSets = true,
            ConnectTimeout = 15
        };

        return new SqlConnection(builder.ConnectionString);
    }

    private static async Task<DataTable> ExecuteQueryAsync(
        SqlConnection conn,
        string sql,
        int timeout = 30)
    {
        using var cmd = new SqlCommand(sql, conn)
        {
            CommandTimeout = timeout
        };

        return await ExecuteQueryAsync(cmd);
    }

    private static async Task<DataTable> ExecuteQueryAsync(SqlCommand cmd)
    {
        await using var reader = await cmd.ExecuteReaderAsync();

        var dt = new DataTable();
        dt.Load(reader);

        return dt;
    }

    private static async Task ExecuteNonQueryAsync(
        SqlConnection conn,
        string sql,
        int timeout = 30)
    {
        using var cmd = new SqlCommand(sql, conn)
        {
            CommandTimeout = timeout
        };

        await cmd.ExecuteNonQueryAsync();
    }

    private static string MakeDbName(string baseName)
    {
        string clean = Sanitize(baseName, 50);

        if (string.IsNullOrWhiteSpace(clean))
            clean = "Database";

        return $"{clean}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
    }

    private static string Sanitize(string value, int maxLength = 80)
    {
        var sb = new StringBuilder(value.Length);

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                sb.Append(c);
            else
                sb.Append('_');
        }

        string result = sb.ToString().Trim('_');

        if (result.Length > maxLength)
            result = result[..maxLength].Trim('_');

        return string.IsNullOrWhiteSpace(result)
            ? "item"
            : result;
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    private static string Escape(string value) => value.Replace("'", "''");

    private void ShowStatus(string message) => _status.Text = message;

    private void ShowError(string title, Exception ex)
    {
        Exception root = ex;

        while (root.InnerException is not null)
            root = root.InnerException;

        MessageBox.Show(
            this,
            $"{title}\n\n{root.Message}",
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private sealed record TableInfo(string Database, string Schema, string Table);
}
