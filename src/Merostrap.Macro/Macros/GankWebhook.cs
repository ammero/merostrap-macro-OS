using System;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Merostrap.Macros;

public sealed record GankAlert(Uri Endpoint, string Message, string Authorization = "", string WindowId = "", byte[]? Screenshot = null);

internal static class GankWebhook
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    public static Uri Validate(string url, string message)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.Host is not ("discord.com" or "discordapp.com" or "canary.discord.com" or "ptb.discord.com") ||
            !Regex.IsMatch(uri.AbsolutePath, @"^/api(?:/v[0-9]+)?/webhooks/[0-9]+/[A-Za-z0-9_-]+$"))
            throw new ArgumentException("Enter a valid Discord webhook URL.");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 2000) throw new ArgumentException("Message must contain 1 to 2000 characters.");
        var builder = new UriBuilder(uri) { Query = "wait=true", Fragment = "" };
        return builder.Uri;
    }
    public static GankAlert Create(Dictionary<string,string> values)
    {
        var message=values.GetValueOrDefault("message", "");
        if (string.IsNullOrWhiteSpace(message) || message.Length>2000) throw new ArgumentException("Message must contain 1 to 2000 characters.");
        var mode=values.GetValueOrDefault("delivery_mode", "0");
        var window=values.GetValueOrDefault("capture_window", "");
        if (mode=="0") return new(Validate(values.GetValueOrDefault("webhook_url", ""),message),message,WindowId:window);
        if (mode!="1") throw new ArgumentException("Choose a delivery mode.");
        var channel=values.GetValueOrDefault("channel_id", "").Trim();
        if (!Regex.IsMatch(channel,@"^[0-9]{17,20}$")) throw new ArgumentException("Enter a Discord channel ID.");
        var secret=GankSecrets.Read(values.GetValueOrDefault("discord_token", "")).Trim();
        if (secret.Length is < 20 or > 4096 || secret.Any(char.IsWhiteSpace) || secret.Any(char.IsControl)) throw new ArgumentException("Enter a valid Discord token.");
        return new(new Uri("https://discord.com/api/v10/channels/"+channel+"/messages"),message,secret,window);
    }
    public static Task SendAsync(Uri uri, string message, CancellationToken token) => SendAsync(Client, uri, message, token);
    internal static Task SendAsync(HttpClient client, Uri uri, string message, CancellationToken token) => SendAlertAsync(client,new(uri,message),token);
    public static Task SendAlertAsync(GankAlert alert,CancellationToken token) => SendAlertAsync(Client,alert,token);
    internal static async Task SendAlertAsync(HttpClient client,GankAlert alert,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,alert.Endpoint);
        if (alert.Authorization.Length>0) request.Headers.TryAddWithoutValidation("Authorization",alert.Authorization);
        if (alert.Screenshot is { Length: > 0 } png)
        {
            if (png.Length>8*1024*1024) throw new InvalidOperationException("Screenshot is too large to attach.");
            var body=new MultipartFormDataContent();
            body.Add(new StringContent(JsonSerializer.Serialize(new {content=alert.Message, attachments=new[]{new {id=0,filename="alert.png"}}}),Encoding.UTF8,"application/json"),"payload_json");
            var image=new ByteArrayContent(png);image.Headers.ContentType=new("image/png");body.Add(image,"files[0]","alert.png");request.Content=body;
        }
        else request.Content=JsonContent.Create(new {content=alert.Message});
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
            if (response.StatusCode==HttpStatusCode.TooManyRequests) throw new InvalidOperationException("Discord rate limited the alert. Try again later.");
            if (response.StatusCode==HttpStatusCode.Unauthorized) throw new InvalidOperationException("Discord rejected the token or webhook credentials.");
            if (response.StatusCode==HttpStatusCode.Forbidden) throw new InvalidOperationException("Discord denied access to this channel.");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Discord rejected the alert (HTTP {(int)response.StatusCode}).");
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Could not reach Discord. Check your connection."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException("Discord alert request timed out."); }
    }
}
