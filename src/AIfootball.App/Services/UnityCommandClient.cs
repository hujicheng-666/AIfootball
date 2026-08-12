namespace AIfootball.App.Services;

public static class UnityCommandClient
{
    public static async Task SendAsync(string workspaceDirectory, string command)
    {
        var path = Path.Combine(workspaceDirectory, "runtime", "data", "wpf-unity-command.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, command);
    }
}
