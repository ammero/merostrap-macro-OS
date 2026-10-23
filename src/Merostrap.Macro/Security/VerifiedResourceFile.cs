using System.Security.Cryptography;

namespace Merostrap.Security;

/// <summary>Authenticates an extracted resource and holds a read lease against Windows replacement/writes.</summary>
public static class VerifiedResourceFile
{
    public static FileStream Acquire(Stream resource, string destination)
    {
        ArgumentNullException.ThrowIfNull(resource);
        destination = Path.GetFullPath(destination);
        RejectLinks(destination);
        byte[] expected = SHA256.HashData(resource);
        resource.Position = 0;
        if (File.Exists(destination))
        {
            var existing = OpenVerified(destination, resource.Length, expected);
            if (existing is not null) return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        RejectLinks(destination);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            RejectLinks(destination);
            File.Move(temporary, destination, overwrite: true);
            return OpenVerified(destination, resource.Length, expected)
                ?? throw new InvalidDataException("Bundled resource changed during extraction.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream? OpenVerified(string path, long length, byte[] expected)
    {
        RejectLinks(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            RejectLinks(path);
            if (file.Length == length && CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), expected))
            {
                file.Position = 0;
                return file;
            }
        }
        catch { file.Dispose(); throw; }
        file.Dispose();
        return null;
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked resource cache paths are not supported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
