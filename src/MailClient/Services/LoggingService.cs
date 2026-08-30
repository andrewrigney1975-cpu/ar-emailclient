namespace MailClient.Services;

/// Structured warning/error/trace sink -> app.log next to the exe. crash.log (fatal/unhandled)
/// is written separately by App.xaml.cs.
public static class LoggingService
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "app.log");
    private static readonly object Lock = new();

    public static void Warn(string source, Exception ex) => Write("WARN", source, ex.ToString());

    public static void Error(string source, Exception ex) => Write("ERROR", source, ex.ToString());

    public static void Info(string source, string message) => Write("INFO", source, message);

    private static void Write(string level, string source, string message)
    {
        lock (Lock)
        {
            try
            {
                File.AppendAllText(FilePath, $"[{DateTime.Now:O}] {level} {source}\n{message}\n\n");
            }
            catch
            {
                // best effort
            }
        }
    }
}
