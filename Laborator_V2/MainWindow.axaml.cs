using Avalonia.Controls;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text.Json;

namespace Laborator_V2;

public partial class MainWindow : Window
{
    private string _connStr = "";
    private string _parentTable = "";
    private string _childTable = "";

    private string _parentPk = "";
    private string _childPk = "";
    private string _childFk = "";

    private DataTable _parentData = new DataTable();
    private DataTable _childData = new DataTable();

    private Dictionary<string, object?>? _selectedParentRow = null;
    private Dictionary<string, object?>? _selectedChildRow = null;

    private Dictionary<string, TextBox> _inputFields = new();


    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        LoadParentData();
    }



    private void LoadConfig()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath)) configPath = "appsettings.json";

        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath))
                     ?? new Dictionary<string, string>();

        _connStr    = config.GetValueOrDefault("connectionString", "");
        _parentTable = config.GetValueOrDefault("parentTable", "");
        _childTable  = config.GetValueOrDefault("childTable", "");
    }
    

    private void ResolveKeys(SqlConnection conn)
    {
        if (!string.IsNullOrWhiteSpace(_parentPk) &&
            !string.IsNullOrWhiteSpace(_childPk)  &&
            !string.IsNullOrWhiteSpace(_childFk))
            return;

        _parentPk = FindPrimaryKey(conn, _parentTable) ?? "";
        _childPk  = FindPrimaryKey(conn, _childTable)  ?? "";
        _childFk  = FindForeignKeyToParent(conn, _childTable, _parentTable) ?? "";

        if (string.IsNullOrWhiteSpace(_parentPk) ||
            string.IsNullOrWhiteSpace(_childPk)  ||
            string.IsNullOrWhiteSpace(_childFk))
            StatusText.Text = "Nu am putut determina toate cheile din DB.";
    }

    private static (string Schema, string Name) ParseTableName(string tableName)
    {
        var cleaned = tableName.Replace("[", "").Replace("]", "").Trim();
        var parts   = cleaned.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
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
 AND tc.TABLE_SCHEMA    = ku.TABLE_SCHEMA
WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
  AND tc.TABLE_SCHEMA = @schema
  AND tc.TABLE_NAME   = @name
ORDER BY ku.ORDINAL_POSITION;";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@name",   name);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    private static string? FindForeignKeyToParent(SqlConnection conn, string childTable, string parentTable)
    {
        var (childSchema,  childName)  = ParseTableName(childTable);
        var (parentSchema, parentName) = ParseTableName(parentTable);
        const string sql = @"
SELECT c_child.name
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id          = fkc.constraint_object_id
JOIN sys.tables  t_parent         ON fkc.referenced_object_id = t_parent.object_id
JOIN sys.schemas s_parent         ON t_parent.schema_id    = s_parent.schema_id
JOIN sys.tables  t_child          ON fkc.parent_object_id  = t_child.object_id
JOIN sys.schemas s_child          ON t_child.schema_id     = s_child.schema_id
JOIN sys.columns c_child          ON fkc.parent_object_id  = c_child.object_id
                                 AND fkc.parent_column_id  = c_child.column_id
WHERE s_parent.name = @parentSchema AND t_parent.name = @parentName
  AND s_child.name  = @childSchema  AND t_child.name  = @childName
ORDER BY fkc.constraint_column_id;";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@parentSchema", parentSchema);
        cmd.Parameters.AddWithValue("@parentName",   parentName);
        cmd.Parameters.AddWithValue("@childSchema",  childSchema);
        cmd.Parameters.AddWithValue("@childName",    childName);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }


    private static void BuildColumns(DataGrid grid, DataTable table)
    {
        grid.ItemsSource = null;
        grid.AutoGenerateColumns = false;
        grid.Columns.Clear();

        foreach (DataColumn col in table.Columns)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header  = col.ColumnName,
                Width   = new DataGridLength(1, DataGridLengthUnitType.Star),
                Binding = new Avalonia.Data.Binding($"[{col.ColumnName}]")
            });
        }
    }
    
    
    private static void SetRows(DataGrid grid, DataTable table)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (DataRow row in table.Rows)
        {
            var dict = new Dictionary<string, object?>();
            foreach (DataColumn col in table.Columns)
                dict[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
            rows.Add(dict);
        }
        grid.ItemsSource = rows;
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private void LoadParentData()
    {
        try
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            ResolveKeys(conn);

            _parentData = new DataTable();
            using var adapter = new SqlDataAdapter($"SELECT * FROM {_parentTable}", conn);
            adapter.MissingSchemaAction = MissingSchemaAction.AddWithKey;
            adapter.Fill(_parentData);

            BuildColumns(ParentGrid, _parentData);
            SetRows(ParentGrid, _parentData);

            if (_parentData.Rows.Count > 0)
                ParentGrid.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Eroare la încărcarea părinților: " + ex.Message;
        }
    }

    private void ReloadChildData()
    {
        if (_selectedParentRow == null) return;

        try
        {
            var parentPkValue = _selectedParentRow[_parentPk] ?? DBNull.Value;

            using var conn = new SqlConnection(_connStr);
            conn.Open();
            ResolveKeys(conn);

            _childData = new DataTable();
            using var cmd = new SqlCommand(
                $"SELECT * FROM {_childTable} WHERE {_childFk} = @pk", conn);
            cmd.Parameters.AddWithValue("@pk", parentPkValue);

            using var adapter = new SqlDataAdapter(cmd);
            adapter.MissingSchemaAction = MissingSchemaAction.AddWithKey;
            adapter.Fill(_childData);

            // If no rows came back, pull schema separately so columns are still visible.
            if (_childData.Columns.Count == 0)
            {
                using var schemaAdapter = new SqlDataAdapter(
                    $"SELECT TOP 0 * FROM {_childTable}", conn);
                schemaAdapter.MissingSchemaAction = MissingSchemaAction.AddWithKey;
                schemaAdapter.FillSchema(_childData, SchemaType.Source);
            }

            BuildColumns(ChildGrid, _childData);
            SetRows(ChildGrid, _childData);
            GenerateDynamicForm(_childData);

        }
        catch (Exception ex)
        {
            StatusText.Text = "Eroare la încărcarea copiilor: " + ex.Message;
        }
    }

    // ── Selection handlers ────────────────────────────────────────────────────

    private void ParentGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ParentGrid.SelectedItem is Dictionary<string, object?> row)
        {
            _selectedParentRow = row;
            _selectedChildRow  = null;
            ReloadChildData();
        }
    }

    private void ChildGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ChildGrid.SelectedItem is Dictionary<string, object?> row)
        {
            _selectedChildRow = row;
            // Populate form fields with selected row values
            foreach (var kvp in _inputFields)
            {
                var val = row.TryGetValue(kvp.Key, out var v) ? v?.ToString() ?? "" : "";
                kvp.Value.Text = val;
            }
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public void BtnDelete_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedChildRow == null)
        {
            StatusText.Text = "Selectează un rând din tabelul copil.";
            return;
        }

        try
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();

            using var cmd = new SqlCommand(
                $"DELETE FROM {_childTable} WHERE {_childPk} = @pk", conn);
            cmd.Parameters.AddWithValue("@pk", _selectedChildRow[_childPk] ?? DBNull.Value);

            cmd.ExecuteNonQuery();
            StatusText.Text = "Rând șters cu succes.";
            _selectedChildRow = null;
            ReloadChildData();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Eroare la ștergere: " + ex.Message;
        }
    }

    // ── Dynamic form ──────────────────────────────────────────────────────────

    private void GenerateDynamicForm(DataTable schema)
    {
        DynamicFormPanel.Children.Clear();
        _inputFields.Clear();

        foreach (DataColumn col in schema.Columns)
        {
            var panel = new StackPanel { Margin = new Avalonia.Thickness(5), Width = 180 };

            panel.Children.Add(new TextBlock
            {
                Text       = col.ColumnName,
                FontWeight = Avalonia.Media.FontWeight.Medium,
                Margin     = new Avalonia.Thickness(0, 0, 0, 3)
            });

            var textBox = new TextBox();

            if (col.ColumnName == _childFk && _selectedParentRow != null)
            {
                textBox.Text      = _selectedParentRow[_parentPk]?.ToString();
                textBox.IsReadOnly = true;
            }

            panel.Children.Add(textBox);
            DynamicFormPanel.Children.Add(panel);
            _inputFields[col.ColumnName] = textBox;
        }
    }

    // ── Insert ────────────────────────────────────────────────────────────────

    public void BtnInsert_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedParentRow == null)
        {
            StatusText.Text = "Selectează un rând din tabelul părinte.";
            return;
        }

        try
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();

            var columns    = new List<string>();
            var paramNames = new List<string>();

            foreach (var kvp in _inputFields)
            {
                columns.Add(kvp.Key);
                paramNames.Add("@" + kvp.Key);
            }

            var query = $"INSERT INTO {_childTable} ({string.Join(", ", columns)}) " +
                        $"VALUES ({string.Join(", ", paramNames)})";

            using var cmd = new SqlCommand(query, conn);
            foreach (var kvp in _inputFields)
            {
                cmd.Parameters.AddWithValue("@" + kvp.Key,
                    string.IsNullOrWhiteSpace(kvp.Value.Text)
                        ? DBNull.Value
                        : (object)kvp.Value.Text);
            }

            cmd.ExecuteNonQuery();
            StatusText.Text = "Rând adăugat cu succes.";
            ReloadChildData();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Eroare la adăugare: " + ex.Message;
        }
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public void BtnUpdate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedChildRow == null)
        {
            StatusText.Text = "Selectează un rând din tabelul copil.";
            return;
        }

        try
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();

            var setClauses = new List<string>();
            foreach (var kvp in _inputFields)
            {
                if (kvp.Key == _childPk || kvp.Key == _childFk) continue;
                setClauses.Add($"{kvp.Key} = @{kvp.Key}");
            }

            var query = $"UPDATE {_childTable} SET {string.Join(", ", setClauses)} " +
                        $"WHERE {_childPk} = @pk";

            using var cmd = new SqlCommand(query, conn);
            foreach (var kvp in _inputFields)
            {
                if (kvp.Key == _childPk || kvp.Key == _childFk) continue;
                cmd.Parameters.AddWithValue("@" + kvp.Key,
                    string.IsNullOrWhiteSpace(kvp.Value.Text)
                        ? DBNull.Value
                        : (object)kvp.Value.Text);
            }
            cmd.Parameters.AddWithValue("@pk", _selectedChildRow[_childPk] ?? DBNull.Value);

            cmd.ExecuteNonQuery();
            StatusText.Text = "Rând modificat cu succes.";
            ReloadChildData();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Eroare la modificare: " + ex.Message;
        }
    }
}