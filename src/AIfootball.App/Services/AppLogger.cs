namespace AIfootball.App.Services;

/// <summary>Writes diagnostics to a rolling local file instead of the UI.</summary>
public static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static string _directory = Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Initialize(string workspaceDirectory)
    {
        _directory = Path.Combine(workspaceDirectory, "logs");
        Directory.CreateDirectory(_directory);
    }

    public static void Write(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"aifootball-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToUpperInvariant()}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never interrupt an analysis task.
        }
    }
}
