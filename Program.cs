namespace DamnWindows11;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\DamnWindows11.SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Log.Init();
        Application.Run(new TrayApp());
    }
}
