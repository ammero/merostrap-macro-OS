namespace Merostrap;

public static class StandalonePaths
{
    public static string ConfigRoot { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MerostrapMacro");
}
