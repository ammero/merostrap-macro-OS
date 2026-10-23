using System;
using System.Security.Cryptography;
using System.Text;
namespace Merostrap.Macros;
internal static class GankSecrets
{
    public static string Protect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Token storage requires Windows.");
        try { return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { throw new InvalidOperationException("Could not protect the Discord token."); }
    }
    public static string Read(string value)
    {
        if (!value.StartsWith("dpapi:", StringComparison.Ordinal)) return value;
        if (!OperatingSystem.IsWindows()) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return ""; }
    }
}
