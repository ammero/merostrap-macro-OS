using Merostrap.Macros.Health;
using System.Text.RegularExpressions;
namespace Merostrap.Macros;

public sealed record CharismaFrame(string Quote, string Entered, bool Reliable=true, HealthRegion? InputArea=null);
public interface ICharismaPromptSource : IDisposable { CharismaFrame Read(CancellationToken token); }

public static class CharismaText
{
    public static string[] Tokens(string text) => Regex.Matches(text.Replace("\u2019", "'").Replace("'", ""), @"[\p{L}\p{N}]+").Select(m=>m.Value.ToLowerInvariant()).ToArray();
}
