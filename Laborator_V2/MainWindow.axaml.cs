using Avalonia.Controls;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Laborator_V2;

public partial class MainWindow : Window
{
    private string _connStr = "";
    private string _parentTable = "";
    private string _childTable = "";

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        TestConnection();
    }

    private void LoadConfig()
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText("appsettings.json")
        );
        _connStr = config!["connectionString"];
        _parentTable = config["parentTable"];
        _childTable = config["childTable"];
    }

    private void TestConnection()
    {
        var status = this.FindControl<TextBlock>("StatusText")!;
        try
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            status.Text = $"✅ Connected! Parent: {_parentTable}, Child: {_childTable}";
        }
        catch (Exception ex)
        {
            status.Text = "❌ Failed: " + ex.Message;
        }
    }
}