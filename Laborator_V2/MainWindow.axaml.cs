using Avalonia.Controls;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Laborator_V2;

public partial class MainWindow : Window
{
    private string _connStr = "";
    private string _parentTable = "";
    private string _childTable = "";

    // Key names loaded from appsettings.json.
    private string _parentPk = "";
    private string _childPk = "";
    private string _childFk = "";

    private DataSet _dataSet = new DataSet();
    private Dictionary<string, object?>? _selectedParentRow = null;
    private Dictionary<string, object?>? _selectedChildRow = null;

    private const string PlaceholderRowKey = "__placeholder__";

    // Guard flag: prevents ChildGrid_SelectionChanged from firing
    // while columns are being rebuilt during ReloadChildData().
    private bool _isLoadingChildData = false;

    // Dicționar pentru a păstra referințele către TextBox-urile generate dinamic
    private Dictionary<string, TextBox> _inputFields = new Dictionary<string, TextBox>();

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();

        LoadParentData();
    }

    private void LoadConfig()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
            configPath = "appsettings.json";

        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath))
                     ?? new Dictionary<string, string>();

        _connStr = config.GetValueOrDefault("connectionString", "");
        _parentTable = config.GetValueOrDefault("parentTable", "");
        _childTable = config.GetValueOrDefault("childTable", "");

        // Keys are resolved dynamically from the database schema.
        _parentPk = "";
        _childPk = "";
        _childFk = "";
    }



    private void LoadParentData()
    {
        try
        {
            Console.WriteLine($"[DB] Connecting: {_connStr}");
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            Console.WriteLine("[DB] Connected (parent load)");

            ResolveKeys(conn);

            var parentTable = EnsureTable(_parentTable);
            EnsureTableSchema(conn, parentTable, _parentTable);

            Console.WriteLine($"[DB] Query parents: SELECT * FROM {_parentTable}");
            using var cmd = new SqlCommand($"SELECT * FROM {_parentTable}", conn);
            using var reader = cmd.ExecuteReader();
            parentTable.Load(reader);
            Console.WriteLine($"[DB] Parents loaded: {parentTable.Rows.Count} rows");

            ConfigureGridColumns(ParentGrid, parentTable);

            // Auto-select the first row so child data loads without extra clicks.
            if (parentTable.Rows.Count > 0)
                ParentGrid.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Parent load error: {ex}");
            StatusText.Text = "❌ Eroare la încărcarea părinților: " + ex.Message;
        }
    }

    private DataTable EnsureTable(string tableName)
    {
        if (_dataSet.Tables.Contains(tableName))
        {
            var table = _dataSet.Tables[tableName];
            table.Clear();
            return table;
        }

        var newTable = new DataTable(tableName);
        _dataSet.Tables.Add(newTable);
        return newTable;
    }

    private void EnsureTableSchema(SqlConnection conn, DataTable table, string tableName)
    {
        if (table.Columns.Count > 0)
            return;

        Console.WriteLine($"[DB] Loading schema for {tableName}");
        using var schemaCmd = new SqlCommand($"SELECT TOP 0 * FROM {tableName}", conn);
        using var schemaReader = schemaCmd.ExecuteReader();
        table.Load(schemaReader);
    }

    private async void ReloadChildData()
    {
        if (_selectedParentRow == null) return;

        _isLoadingChildData = true;

        // Snapshot values needed on the background thread before leaving the UI thread.
        var parentPkValue = _selectedParentRow[_parentPk] ?? DBNull.Value;
        var connStr = _connStr;
        var parentTable = _parentTable;
        var childTable = _childTable;

        DataTable? childTableData = null;
        string? errorMessage = null;

        try
        {
            // Run all blocking DB work on a background thread so the Avalonia UI
            // dispatcher stays free. This prevents the race where Columns.Clear()
            // and ItemsSource assignment interleave with pending layout passes.
            childTableData = await System.Threading.Tasks.Task.Run(() =>
            {
                Console.WriteLine($"[DB] Connecting (background): {connStr}");
                using var conn = new SqlConnection(connStr);
                conn.Open();

                // ResolveKeys is safe to call here; it only reads _parentPk/_childPk/_childFk
                // which are set once and never mutated concurrently.
                ResolveKeys(conn);
                if (string.IsNullOrEmpty(_parentPk) || string.IsNullOrEmpty(_childFk))
                    throw new InvalidOperationException("Nu am putut determina PK/FK din DB.");

                var dt = new DataTable(childTable);

                Console.WriteLine($"[DB] Query children WHERE {_childFk} = {parentPkValue}");
                using var cmd = new SqlCommand(
                    $"SELECT * FROM {childTable} WHERE {_childFk} = @pk", conn);
                cmd.Parameters.AddWithValue("@pk", parentPkValue);

                using var adapter = new SqlDataAdapter(cmd);
                adapter.MissingSchemaAction = System.Data.MissingSchemaAction.AddWithKey;
                adapter.Fill(dt);
                Console.WriteLine($"[DB] Children loaded: {dt.Rows.Count} rows");

                // Safety net for 0-row results on some driver versions.
                if (dt.Columns.Count == 0)
                {
                    using var schemaCmd = new SqlCommand($"SELECT TOP 0 * FROM {childTable}", conn);
                    using var schemaAdapter = new SqlDataAdapter(schemaCmd);
                    schemaAdapter.MissingSchemaAction = System.Data.MissingSchemaAction.AddWithKey;
                    schemaAdapter.FillSchema(dt, System.Data.SchemaType.Source);
                }

                return dt;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Child load error: {ex}");
            errorMessage = ex.Message;
        }

        // Back on UI thread — update grid and form.
        _isLoadingChildData = true; // keep guard up during UI rebuild
        try
        {
            if (errorMessage != null)
            {
                StatusText.Text = "❌ Eroare la încărcarea copiilor: " + errorMessage;
                return;
            }

            // Sync DataSet.
            if (_dataSet.Tables.Contains(_childTable))
                _dataSet.Tables.Remove(_childTable);
            _dataSet.Tables.Add(childTableData!);

            ConfigureGridColumns(ChildGrid, childTableData!);
            GenerateDynamicForm(childTableData!);
        }
        finally
        {
            _isLoadingChildData = false;
        }
    }

    private void GenerateDynamicForm(DataTable childTableSchema)
    {
        DynamicFormPanel.Children.Clear();
        _inputFields.Clear();

        foreach (DataColumn col in childTableSchema.Columns)
        {
            var panel = new StackPanel { Margin = new Avalonia.Thickness(5), Width = 210 };
            var label = new TextBlock { Text = col.ColumnName, FontWeight = Avalonia.Media.FontWeight.Medium, Margin = new Avalonia.Thickness(0, 0, 0, 3) };
            var textBox = new TextBox { Name = "txt_" + col.ColumnName };

            // Dacă este cheia primară a copilului, o blocăm (de obicei este Identity auto-generat)
            if (col.ColumnName == _childPk)
            {
                textBox.IsReadOnly = true;
                textBox.Watermark = "(Auto-PK)";
            }
            // Dacă este cheia externă, o completăm automat cu valoarea din părinte și o blocăm
            else if (col.ColumnName == _childFk && _selectedParentRow != null)
            {
                textBox.Text = _selectedParentRow[_parentPk]?.ToString();
                textBox.IsReadOnly = true;
            }

            panel.Children.Add(label);
            panel.Children.Add(textBox);
            DynamicFormPanel.Children.Add(panel);

            // Salvăm instanța în dicționar ca să îi putem citi textul ulterior la evenimentele de Click
            _inputFields.Add(col.ColumnName, textBox);
        }
    }

    private void ParentGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ParentGrid.SelectedItem is Dictionary<string, object?> row)
        {
            if (IsPlaceholderRow(row))
                return;

            _selectedParentRow = row;
            _selectedChildRow = null;
            ReloadChildData();
        }
    }

    private void ChildGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Ignore selection-change events that Avalonia fires internally
        // while we are rebuilding columns and ItemsSource.
        if (_isLoadingChildData) return;

        if (ChildGrid.SelectedItem is Dictionary<string, object?> row)
        {
            if (IsPlaceholderRow(row))
                return;

            _selectedChildRow = row;
            // Populate inputs for update/delete.
            foreach (var kvp in _inputFields)
            {
                kvp.Value.Text = row.TryGetValue(kvp.Key, out var value) ? value?.ToString() ?? "" : "";
            }
        }
    }

    // OPERAȚIA DE ADĂUGARE (INSERT)
    private void BtnInsert_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_childPk) || string.IsNullOrEmpty(_childFk))
        {
            StatusText.Text = "⚠️ Setează PK/FK în appsettings.json.";
            return;
        }

        try
        {
            Console.WriteLine($"[DB] Connecting: {_connStr}");
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            Console.WriteLine("[DB] Connected (insert)");

            var columns = new List<string>();
            var paramNames = new List<string>();

            foreach (var kvp in _inputFields)
            {
                if (kvp.Key == _childPk) continue; // Sărim peste cheia primară automată
                columns.Add(kvp.Key);
                paramNames.Add("@" + kvp.Key);
            }

            string query = $"INSERT INTO {_childTable} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)})";
            Console.WriteLine($"[DB] Insert: {query}");
            using var cmd = new SqlCommand(query, conn);

            foreach (var kvp in _inputFields)
            {
                if (kvp.Key == _childPk) continue;
                cmd.Parameters.AddWithValue("@" + kvp.Key, string.IsNullOrWhiteSpace(kvp.Value.Text) ? DBNull.Value : kvp.Value.Text);
            }

            cmd.ExecuteNonQuery();
            StatusText.Text = "✅ Înregistrare copil adăugată cu succes!";
            ReloadChildData();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Insert error: {ex}");
            StatusText.Text = "❌ Eroare la adăugare: " + ex.Message;
        }
    }

    // OPERAȚIA DE MODIFICARE (UPDATE)
    private void BtnUpdate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_childPk) || string.IsNullOrEmpty(_childFk))
        {
            StatusText.Text = "⚠️ Setează PK/FK în appsettings.json.";
            return;
        }

        try
        {
            Console.WriteLine($"[DB] Connecting: {_connStr}");
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            Console.WriteLine("[DB] Connected (update)");

            var updateClauses = new List<string>();
            foreach (var kvp in _inputFields)
            {
                // Nu permitem modificarea cheilor PK sau FK direct din formular
                if (kvp.Key == _childPk || kvp.Key == _childFk) continue;
                updateClauses.Add($"{kvp.Key} = @{kvp.Key}");
            }

            string query = $"UPDATE {_childTable} SET {string.Join(", ", updateClauses)} WHERE {_childPk} = @childPkVal";
            Console.WriteLine($"[DB] Update: {query}");
            using var cmd = new SqlCommand(query, conn);

            foreach (var kvp in _inputFields)
            {
                if (kvp.Key == _childPk || kvp.Key == _childFk) continue;
                cmd.Parameters.AddWithValue("@" + kvp.Key, string.IsNullOrWhiteSpace(kvp.Value.Text) ? DBNull.Value : kvp.Value.Text);
            }
            cmd.Parameters.AddWithValue("@childPkVal", _selectedChildRow[_childPk] ?? DBNull.Value);

            cmd.ExecuteNonQuery();
            StatusText.Text = "✅ Înregistrare copil modificată cu succes!";
            ReloadChildData();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Update error: {ex}");
            StatusText.Text = "❌ Eroare la modificare: " + ex.Message;
        }
    }

    // OPERAȚIA DE ȘTERGERE (DELETE)
    private void BtnDelete_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_childPk))
        {
            StatusText.Text = "⚠️ Setează PK/FK în appsettings.json.";
            return;
        }

        try
        {
            Console.WriteLine($"[DB] Connecting: {_connStr}");
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            Console.WriteLine("[DB] Connected (delete)");

            string query = $"DELETE FROM {_childTable} WHERE {_childPk} = @childPkVal";
            Console.WriteLine($"[DB] Delete: {query}");
            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@childPkVal", _selectedChildRow[_childPk] ?? DBNull.Value);

            cmd.ExecuteNonQuery();
            StatusText.Text = "✅ Înregistrare copil ștearsă cu succes!";
            _selectedChildRow = null;
            ReloadChildData();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Delete error: {ex}");
            StatusText.Text = "❌ Eroare la ștergere: " + ex.Message;
        }
    }

    private void ConfigureGridColumns(DataGrid grid, DataTable table)
    {
        // Detach ItemsSource FIRST so Avalonia doesn't try to re-render
        // while the column collection is in an inconsistent (empty) state.
        // This is the root cause of columns disappearing after selection changes.
        grid.ItemsSource = null;
        grid.AutoGenerateColumns = false;
        grid.Columns.Clear();

        Console.WriteLine($"[DBG] Columns for {table.TableName}: {string.Join(", ", table.Columns.Cast<DataColumn>().Select(c => c.ColumnName))}");

        foreach (DataColumn col in table.Columns)
        {
            // Use sanitized keys for binding to avoid parse issues with spaces/symbols.
            var safeKey = SanitizeColumnKey(col.ColumnName);
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = col.ColumnName,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                Binding = new Avalonia.Data.Binding($"[{safeKey}]")
            });
        }

        var rows = ToRowDictionaries(table);
        if (rows.Count == 0)
            rows.Add(CreatePlaceholderRow(table));

        // Reattach ItemsSource only after all columns are fully defined.
        grid.ItemsSource = rows;
    }

    private void ResolveKeys(SqlConnection conn)
    {
        if (string.IsNullOrWhiteSpace(_parentTable) || string.IsNullOrWhiteSpace(_childTable))
            return;

        if (!string.IsNullOrWhiteSpace(_parentPk) &&
            !string.IsNullOrWhiteSpace(_childPk) &&
            !string.IsNullOrWhiteSpace(_childFk))
            return;

        _parentPk = FindPrimaryKey(conn, _parentTable) ?? "";
        _childPk = FindPrimaryKey(conn, _childTable) ?? "";
        _childFk = FindForeignKeyToParent(conn, _childTable, _parentTable) ?? "";

        Console.WriteLine($"[DB] Keys: parentPk={_parentPk}, childPk={_childPk}, childFk={_childFk}");

        if (string.IsNullOrWhiteSpace(_parentPk) || string.IsNullOrWhiteSpace(_childPk) || string.IsNullOrWhiteSpace(_childFk))
            StatusText.Text = "⚠️ Nu am putut determina toate cheile din DB.";
    }

    private static (string Schema, string Name) ParseTableName(string tableName)
    {
        var cleaned = tableName.Replace("[", "").Replace("]", "").Trim();
        var parts = cleaned.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", cleaned);
    }

    private static string? FindPrimaryKey(SqlConnection conn, string tableName)
    {
        var (schema, name) = ParseTableName(tableName);
        const string sql = @"
SELECT ku.COLUMN_NAME
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
  ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
 AND tc.TABLE_SCHEMA = ku.TABLE_SCHEMA
WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
  AND tc.TABLE_SCHEMA = @schema
  AND tc.TABLE_NAME = @name
ORDER BY ku.ORDINAL_POSITION;";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    private static string? FindForeignKeyToParent(SqlConnection conn, string childTable, string parentTable)
    {
        var (childSchema, childName) = ParseTableName(childTable);
        var (parentSchema, parentName) = ParseTableName(parentTable);

        const string sql = @"
SELECT c_child.name
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables t_parent ON fkc.referenced_object_id = t_parent.object_id
JOIN sys.schemas s_parent ON t_parent.schema_id = s_parent.schema_id
JOIN sys.tables t_child ON fkc.parent_object_id = t_child.object_id
JOIN sys.schemas s_child ON t_child.schema_id = s_child.schema_id
JOIN sys.columns c_child ON fkc.parent_object_id = c_child.object_id AND fkc.parent_column_id = c_child.column_id
WHERE s_parent.name = @parentSchema AND t_parent.name = @parentName
  AND s_child.name = @childSchema AND t_child.name = @childName
ORDER BY fkc.constraint_column_id;";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@parentSchema", parentSchema);
        cmd.Parameters.AddWithValue("@parentName", parentName);
        cmd.Parameters.AddWithValue("@childSchema", childSchema);
        cmd.Parameters.AddWithValue("@childName", childName);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    private static List<Dictionary<string, object?>> ToRowDictionaries(DataTable table)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (DataRow row in table.Rows)
        {
            var dict = new Dictionary<string, object?>();
            foreach (DataColumn col in table.Columns)
            {
                var value = row[col];
                var normalizedValue = value == DBNull.Value ? null : value;
                dict[col.ColumnName] = normalizedValue;

                var safeKey = SanitizeColumnKey(col.ColumnName);
                if (!string.Equals(safeKey, col.ColumnName, StringComparison.Ordinal))
                    dict[safeKey] = normalizedValue;
            }
            rows.Add(dict);
        }
        return rows;
    }

    private static Dictionary<string, object?> CreatePlaceholderRow(DataTable table)
    {
        var dict = new Dictionary<string, object?>
        {
            [PlaceholderRowKey] = true
        };

        foreach (DataColumn col in table.Columns)
        {
            dict[col.ColumnName] = null;

            var safeKey = SanitizeColumnKey(col.ColumnName);
            if (!string.Equals(safeKey, col.ColumnName, StringComparison.Ordinal))
                dict[safeKey] = null;
        }

        return dict;
    }

    private static bool IsPlaceholderRow(Dictionary<string, object?> row)
    {
        return row.TryGetValue(PlaceholderRowKey, out var value) && value is true;
    }

    private static string SanitizeColumnKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "_";

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!char.IsLetterOrDigit(c) && c != '_')
                chars[i] = '_';
        }

        return new string(chars);
    }
}