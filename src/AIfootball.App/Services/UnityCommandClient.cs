namespace AIfootball.App.Services;

public static class UnityCommandClient
{
    private static long _commandSequence;

    public static async Task SendAsync(string workspaceDirectory, string command)
    {
        var path = Path.Combine(workspaceDirectory, "runtime", "data", "wpf-unity-command.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // The Unity bridge watches file content. Prefix every request with a unique
        // sequence number so repeated actions (especially replay) are never ignored.
        var payload = Interlocked.Increment(ref _commandSequence) + "\n" + (command ?? string.Empty);
        await File.WriteAllTextAsync(path, payload);
    }
}
