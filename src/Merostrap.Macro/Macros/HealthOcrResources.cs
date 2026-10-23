namespace Merostrap.Macros;

/// <summary>Extract the bundled OCR engine into this app's local cache.</summary>
public static class HealthOcrResources
{
    private static readonly object Gate = new();
    private static string? _ready;
    // Kept for the process lifetime. Windows denies writes/deletion while a verified file is leased.
    private static readonly List<FileStream> VerifiedFiles = new();

    public static string EnsureAvailable()
    {
        lock (Gate)
        {
            if (_ready is not null) return _ready;
            var assembly = typeof(HealthOcrResources).Assembly;
            var root = Path.Combine(StandalonePaths.ConfigRoot, "Ocr", assembly.ManifestModule.ModuleVersionId.ToString("N"));
            try
            {
                foreach (var relative in new[] { "tessdata/eng.traineddata", "x64/tesseract50.dll", "x64/leptonica-1.82.0.dll" })
                {
                    using var source = assembly.GetManifestResourceStream("Ocr." + relative.Replace('/', '.'))
                        ?? throw new IOException("Missing bundled OCR resource: " + relative);
                    VerifiedFiles.Add(Security.VerifiedResourceFile.Acquire(source, Path.Combine(root, relative)));
                }
                return _ready = root;
            }
            catch
            {
                foreach (var file in VerifiedFiles) file.Dispose();
                VerifiedFiles.Clear();
                throw;
            }
        }
    }
}
