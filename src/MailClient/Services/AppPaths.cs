namespace MailClient.Services;

/// Everything the app writes lives under %LocalAppData%\WinUI3Mail\.
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUI3Mail");

    public static string InData(string fileName)
    {
        Directory.CreateDirectory(DataDirectory);
        return Path.Combine(DataDirectory, fileName);
    }
}
