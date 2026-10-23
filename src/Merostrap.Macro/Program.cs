using Avalonia;

namespace Merostrap;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Merostrap Macro currently supports Windows only.");
            return 1;
        }
        bool smoke = false;
        string? dataDirectory = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--smoke-test") smoke = true;
            else if (args[index] == "--data-dir" && index + 1 < args.Length) dataDirectory = args[++index];
            else
            {
                Console.Error.WriteLine("Usage: Merostrap.Macro [--data-dir DIRECTORY] [--smoke-test]");
                return 2;
            }
        }
        if (smoke && dataDirectory is null)
        {
            Console.Error.WriteLine("Smoke tests require an isolated --data-dir.");
            return 2;
        }
        if (dataDirectory is not null) StandalonePaths.ConfigRoot = Path.GetFullPath(dataDirectory);
        App.SmokeTest = smoke;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return Environment.ExitCode;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
