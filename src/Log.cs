namespace FlashGuard;

static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(GuardSettings.Directory, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(GuardSettings.Directory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 256 * 1024)
                    info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
