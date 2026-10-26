namespace DamnWindows11;

internal static class Log
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamnWindows11");

    public static readonly string FilePath = Path.Combine(Dir, "log.txt");

    private static readonly object Gate = new();

    public static void Init()
    {
        Directory.CreateDirectory(Dir);
        var fi = new FileInfo(FilePath);
        if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}
