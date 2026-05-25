using Avalonia.Controls;
using Microsoft.Data.Sqlite;
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
    private string _initSqlPath = "";

    // Variabile pentru stocarea metadatelor descoperite dinamic
    private string _parentPk = "";
    private string _childPk = "";
    private string _childFk = "";

    private DataSet _dataSet = new DataSet();
    private Dictionary<string, object?>? _selectedParentRow = null;
    private Dictionary<string, object?>? _selectedChildRow = null;

    // Dicționar pentru a păstra referințele către TextBox-urile generate dinamic
    private Dictionary<string, TextBox> _inputFields = new Dictionary<string, TextBox>();

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        EnsureDatabase();

        if (InitDatabaseMetadata())
        {
            LoadParentData();
        }
    }

    private void LoadConfig()
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText("appsettings.json"));
        var builder = new SqliteConnectionStringBuilder(config!["connectionString"]);
        if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(builder.DataSource)))
            builder.DataSource = Path.Combine(AppContext.BaseDirectory, builder.DataSource);

        _connStr = builder.ToString();
        _parentTable = config["parentTable"];
        _childTable = config["childTable"];
        _initSqlPath = Path.Combine(AppContext.BaseDirectory, "QueryTestDb.sql");
    }

    private void EnsureDatabase()
    {
        var dbPath = new SqliteConnectionStringBuilder(_connStr).DataSource;

        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        EnableForeignKeys(conn);

        var hasTables = DatabaseHasTable(conn, _parentTable) && DatabaseHasTable(conn, _childTable);

        if (!hasTables)
        {
            if (File.Exists(_initSqlPath))
            {
                var sql = File.ReadAllText(_initSqlPath);
                foreach (var statement in SplitSqlStatements(sql))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = statement;
                    cmd.ExecuteNonQuery();
                }
            }
            else
            {
                CreateSchemaAndSeed(conn);
            }
        }

        EnsureSeedData(conn);
    }

    private static IEnumerable<string> SplitSqlStatements(string sql)
    {
        foreach (var part in sql.Split(';'))
        {
            var statement = part.Trim();
            if (!string.IsNullOrEmpty(statement))
                yield return statement;
        }
    }

    private static void EnableForeignKeys(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
    }

    // DESCOPERIREA DINAMICĂ A STRUCTURII (Cerinta obligatorie pentru Nota 10)
    private bool InitDatabaseMetadata()
    {
        try
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            EnableForeignKeys(conn);

            _parentPk = GetPrimaryKey(conn, _parentTable);
            _childPk = GetPrimaryKey(conn, _childTable);
            _childFk = GetForeignKey(conn, _childTable, _parentTable);

            if (string.IsNullOrEmpty(_parentPk) || string.IsNullOrEmpty(_childPk) || string.IsNullOrEmpty(_childFk))
            {
                StatusText.Text = "❌ Relațiile 1:M nu au putut fi identificate automat în baza de date.";
                return false;
            }

            StatusText.Text = $"✅ Structură detectată! Părinte PK: {_parentPk} | Copil PK: {_childPk} | FK Legătură: {_childFk}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "❌ Eroare la citirea metadatelor: " + ex.Message;
            return false;
        }
    }

    private static string GetPrimaryKey(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{table}]);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var isPk = Convert.ToInt32(reader["pk"]);
            if (isPk == 1)
                return reader["name"].ToString() ?? "";
        }
        return "";
    }

    private static string GetForeignKey(SqliteConnection conn, string childTable, string parentTable)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA foreign_key_list([{childTable}]);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var refTable = reader["table"].ToString();
            if (string.Equals(refTable, parentTable, StringComparison.OrdinalIgnoreCase))
                return reader["from"].ToString() ?? "";
        }
        return "";
    }

    private void LoadParentData()
    {
        try
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            EnableForeignKeys(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {_parentTable}"; // Concatenarea permisă doar pentru numele tabelelor

            using var reader = cmd.ExecuteReader();
            var parentTable = EnsureTable(_parentTable);
            parentTable.Load(reader);

            ConfigureGridColumns(ParentGrid, parentTable);

            // Auto-select the first row so child data loads without extra clicks.
            if (parentTable.Rows.Count > 0)
                ParentGrid.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "❌ Eroare la încărcarea părinților: " + ex.Message;
        }
    }

    private void ParentGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ParentGrid.SelectedItem is Dictionary<string, object?> row)
        {
            _selectedParentRow = row;
            _selectedChildRow = null;
            ReloadChildData();
        }
    }

    private DataTable EnsureTable(string tableName)
    {
        if (_dataSet.Tables.Contains(tableName))
        {
            var table = _dataSet.Tables[tableName];
            table.Clear();
            table.Columns.Clear();
            return table;
        }

        var newTable = new DataTable(tableName);
        _dataSet.Tables.Add(newTable);
        return newTable;
    }

    private void ReloadChildData()
    {
        if (_selectedParentRow == null) return;

        try
        {
            object parentPkValue = _selectedParentRow[_parentPk] ?? DBNull.Value;

            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            EnableForeignKeys(conn);

            // Păstrăm parametrizarea strictă pentru valori interne
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {_childTable} WHERE {_childFk} = @parentPkValue";
            cmd.Parameters.AddWithValue("@parentPkValue", parentPkValue);

            using var reader = cmd.ExecuteReader();
            var childTableData = EnsureTable(_childTable);
            childTableData.Load(reader);

            ConfigureGridColumns(ChildGrid, childTableData);

            // După ce tabelul s-a încărcat, redesenăm formularul dinamic de jos
            GenerateDynamicForm(childTableData);
        }
        catch (Exception ex)
        {
            StatusText.Text = "❌ Eroare la încărcarea copiilor: " + ex.Message;
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

    private void ChildGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ChildGrid.SelectedItem is Dictionary<string, object?> row)
        {
            _selectedChildRow = row;
            // Populăm textbox-urile cu datele rândului selectat pentru Update/Delete
            foreach (var kvp in _inputFields)
            {
                kvp.Value.Text = row.TryGetValue(kvp.Key, out var value) ? value?.ToString() ?? "" : "";
            }
        }
    }

    // OPERAȚIA DE ADĂUGARE (INSERT)
    private void BtnInsert_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedParentRow == null)
        {
            StatusText.Text = "⚠️ Selectați mai întâi un rând din tabelul părinte!";
            return;
        }

        try
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            EnableForeignKeys(conn);

            var columns = new List<string>();
            var paramNames = new List<string>();

            foreach (var kvp in _inputFields)
            {
                if (kvp.Key == _childPk) continue; // Sărim peste cheia primară automată
                columns.Add(kvp.Key);
                paramNames.Add("@" + kvp.Key);
            }

            string query = $"INSERT INTO {_childTable} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)})";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = query;

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
            StatusText.Text = "❌ Eroare la adăugare: " + ex.Message;
        }
    }

    // OPERAȚIA DE MODIFICARE (UPDATE)
    private void BtnUpdate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedChildRow == null)
        {
            StatusText.Text = "⚠️ Selectați înregistrarea din tabelul copil pe care vreți să o modificați!";
            return;
        }

        try
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            EnableForeignKeys(conn);

            var updateClauses = new List<string>();
            foreach (var kvp in _inputFields)
            {
                // Nu permitem modificarea cheilor PK sau FK direct din formular
                if (kvp.Key == _childPk || kvp.Key == _childFk) continue;
                updateClauses.Add($"{kvp.Key} = @{kvp.Key}");
            }

            string query = $"UPDATE {_childTable} SET {string.Join(", ", updateClauses)} WHERE {_childPk} = @childPkVal";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = query;

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
            StatusText.Text = "❌ Eroare la modificare: " + ex.Message;
        }
    }

    // OPERAȚIA DE ȘTERGERE (DELETE)
    private void BtnDelete_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedChildRow == null)
        {
            StatusText.Text = "⚠️ Selectați înregistrarea din tabelul copil pe care vreți să o ștergeți!";
            return;
        }

        try
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            EnableForeignKeys(conn);

            string query = $"DELETE FROM {_childTable} WHERE {_childPk} = @childPkVal";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = query;
            cmd.Parameters.AddWithValue("@childPkVal", _selectedChildRow[_childPk] ?? DBNull.Value);

            cmd.ExecuteNonQuery();
            StatusText.Text = "✅ Înregistrare copil ștearsă cu succes!";
            _selectedChildRow = null;
            ReloadChildData();
        }
        catch (Exception ex)
        {
            StatusText.Text = "❌ Eroare la ștergere: " + ex.Message;
        }
    }

    private void ConfigureGridColumns(DataGrid grid, DataTable table)
    {
        grid.AutoGenerateColumns = false;
        grid.Columns.Clear();

        foreach (DataColumn col in table.Columns)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = col.ColumnName,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                Binding = new Avalonia.Data.Binding($"[{col.ColumnName}]")
            });
        }

        grid.ItemsSource = ToRowDictionaries(table);
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
                dict[col.ColumnName] = value == DBNull.Value ? null : value;
            }
            rows.Add(dict);
        }
        return rows;
    }

    private static bool DatabaseHasTable(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private static void CreateSchemaAndSeed(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Parents (
    ParentId INTEGER PRIMARY KEY,
    Name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Children (
    ChildId INTEGER PRIMARY KEY,
    ParentId INTEGER NOT NULL,
    Name TEXT NOT NULL,
    FOREIGN KEY (ParentId) REFERENCES Parents(ParentId)
);";
            cmd.ExecuteNonQuery();
        }

        EnsureSeedData(conn);
    }

    private static void EnsureSeedData(SqliteConnection conn)
    {
        if (!DatabaseHasTable(conn, "Parents") || !DatabaseHasTable(conn, "Children"))
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO Parents (ParentId, Name) VALUES (1, 'Parent 1');
INSERT OR IGNORE INTO Parents (ParentId, Name) VALUES (2, 'Parent 2');

INSERT OR IGNORE INTO Children (ChildId, ParentId, Name) VALUES (1, 1, 'Child A');
INSERT OR IGNORE INTO Children (ChildId, ParentId, Name) VALUES (2, 1, 'Child B');";
        cmd.ExecuteNonQuery();
    }
}