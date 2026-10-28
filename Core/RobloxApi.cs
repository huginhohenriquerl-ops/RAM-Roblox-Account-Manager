using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RAM.Core;

public sealed class RobloxApiException : Exception
{
    public bool CookieInvalid { get; }

    public RobloxApiException(string message, bool cookieInvalid = false) : base(message) => CookieInvalid = cookieInvalid;
}

public sealed record RobloxUser(long Id, string Name, string DisplayName);

public sealed record GameInfo(long PlaceId, long UniverseId, string Name, string Creator, long Playing, string? IconUrl);

public sealed class RobloxApi
{
    readonly HttpClient _http;

    public RobloxApi()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>Accepts the raw value, "name=value" pairs or a whole Cookie header and returns just the token.</summary>
    public static string NormalizeCookie(string raw)
    {
        var s = raw.Trim().Trim('"', '\'').Trim();
        const string key = ".ROBLOSECURITY=";
        int idx = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s[(idx + key.Length)..];
        int semi = s.IndexOf(';');
        if (semi >= 0) s = s[..semi];
        return s.Trim().Trim('"', '\'');
    }

    static HttpRequestMessage Request(HttpMethod method, string url, string? cookie, string? csrf = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (cookie != null) request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
        if (csrf != null) request.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
        request.Headers.Referrer = new Uri("https://www.roblox.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
        return request;
    }

    /// <summary>
    /// Retries rate limits (429) and transient server errors (5xx) with backoff, honouring Retry-After.
    /// The factory is called again for each attempt because a request message can only be sent once.
    /// </summary>
    async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var response = await _http.SendAsync(make(), ct);
            int code = (int)response.StatusCode;
            bool transient = code == 429 || code >= 500;
            if (!transient || attempt >= 3) return response;

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1.5 * attempt);
            if (wait > TimeSpan.FromSeconds(10)) wait = TimeSpan.FromSeconds(10);
            response.Dispose();
            Log.Info($"roblox api {code}, retrying in {wait.TotalSeconds:0.#}s");
            await Task.Delay(wait, ct);
        }
    }

    Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken ct) =>
        SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);

    /// <summary>Sends an authenticated request, transparently performing Roblox's X-CSRF-TOKEN handshake.</summary>
    async Task<HttpResponseMessage> SendAuthedAsync(Account account, Func<string?, HttpRequestMessage> make, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var response = await SendWithRetryAsync(() => make(account.CsrfToken), ct);
            if (response.StatusCode == HttpStatusCode.Forbidden &&
                response.Headers.TryGetValues("x-csrf-token", out var values))
            {
                var token = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(token) && token != account.CsrfToken)
                {
                    account.CsrfToken = token;
                    response.Dispose();
                    continue;
                }
            }
            return response;
        }
        throw new RobloxApiException("O Roblox recusou o token CSRF.");
    }

    public async Task<RobloxUser?> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => Request(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (!response.IsSuccessStatusCode)
            throw new RobloxApiException($"O Roblox respondeu {(int)response.StatusCode} ao validar a conta.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new RobloxUser(root.GetProperty("id").GetInt64(), root.GetProperty("name").GetString() ?? "",
            root.GetProperty("displayName").GetString() ?? "");
    }

    public async Task<Dictionary<long, string>> GetHeadshotsAsync(IEnumerable<long> userIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in userIds.Distinct().Chunk(100))
        {
            var url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?size=150x150&format=Png&isCircular=false&userIds=" +
                      string.Join(",", chunk);
            using var response = await GetWithRetryAsync(url, ct);
            if (!response.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var image = item.TryGetProperty("imageUrl", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(image)) result[item.GetProperty("targetId").GetInt64()] = image;
            }
        }
        return result;
    }

    /// <summary>One-time authentication ticket that the Roblox client exchanges for a session.</summary>
    public async Task<string> GetAuthTicketAsync(Account account, CancellationToken ct = default)
    {
        using var response = await SendAuthedAsync(account, csrf =>
        {
            var request = Request(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/", account.Cookie, csrf,
                new StringContent("{}", Encoding.UTF8, "application/json"));
            request.Headers.TryAddWithoutValidation("RBXAuthenticationNegotiation", "1");
            return request;
        }, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new RobloxApiException("Cookie inválido ou expirado.", cookieInvalid: true);
        if (response.Headers.TryGetValues("rbx-authentication-ticket", out var tickets))
        {
            var ticket = tickets.FirstOrDefault();
            if (!string.IsNullOrEmpty(ticket)) return ticket;
        }
        throw new RobloxApiException($"Não foi possível gerar o ticket de login ({(int)response.StatusCode}).");
    }

    public async Task<GameInfo?> GetGameInfoAsync(long placeId, CancellationToken ct = default)
    {
        using var universeResponse = await GetWithRetryAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe", ct);
        if (!universeResponse.IsSuccessStatusCode) return null;
        using var universeDoc = JsonDocument.Parse(await universeResponse.Content.ReadAsStringAsync(ct));
        if (!universeDoc.RootElement.TryGetProperty("universeId", out var universeEl) || universeEl.ValueKind != JsonValueKind.Number)
            return null;
        long universeId = universeEl.GetInt64();

        var gameTask = _http.GetStringAsync($"https://games.roblox.com/v1/games?universeIds={universeId}", ct);
        var iconTask = _http.GetStringAsync(
            $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false", ct);

        // Await the optional icon request first so a failure there is always observed.
        string? iconJson = null;
        try { iconJson = await iconTask; } catch (Exception ex) when (ex is not OperationCanceledException) { }

        using var gameDoc = JsonDocument.Parse(await gameTask);
        var data = gameDoc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) return null;
        var game = data[0];

        string? icon = null;
        if (iconJson != null)
        {
            try
            {
                using var iconDoc = JsonDocument.Parse(iconJson);
                var icons = iconDoc.RootElement.GetProperty("data");
                if (icons.GetArrayLength() > 0) icon = icons[0].GetProperty("imageUrl").GetString();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { /* icon is optional */ }
        }

        return new GameInfo(placeId, universeId,
            game.GetProperty("name").GetString() ?? $"Place {placeId}",
            game.TryGetProperty("creator", out var creator) ? creator.GetProperty("name").GetString() ?? "" : "",
            game.TryGetProperty("playing", out var playing) && playing.ValueKind == JsonValueKind.Number ? playing.GetInt64() : 0,
            icon);
    }

    /// <summary>
    /// Resolves roblox.com/share links: "Server" gives a private server link code, "ExperienceInvite" gives the
    /// place and instance (job id) of the friend who shared it.
    /// </summary>
    public async Task<(long PlaceId, string? LinkCode, string? JobId)> ResolveShareLinkAsync(Account account, string code,
        string linkType, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { linkId = code, linkType });
        using var response = await SendAuthedAsync(account, csrf => Request(HttpMethod.Post,
            "https://apis.roblox.com/sharelinks/v1/resolve-link", account.Cookie, csrf,
            new StringContent(body, Encoding.UTF8, "application/json")), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new RobloxApiException("Cookie inválido ou expirado.", cookieInvalid: true);
        if (!response.IsSuccessStatusCode)
            throw new RobloxApiException($"Link de servidor inválido ({(int)response.StatusCode}).");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("privateServerInviteData", out var invite) && invite.ValueKind == JsonValueKind.Object)
        {
            var placeId = invite.GetProperty("placeId").GetInt64();
            var linkCode = invite.GetProperty("linkCode").GetString();
            if (placeId > 0 && !string.IsNullOrEmpty(linkCode)) return (placeId, linkCode, null);
        }
        if (doc.RootElement.TryGetProperty("experienceInviteData", out var experience) && experience.ValueKind == JsonValueKind.Object)
        {
            var placeId = experience.TryGetProperty("placeId", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
            var instance = experience.TryGetProperty("instanceId", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
            if (placeId > 0) return (placeId, null, string.IsNullOrEmpty(instance) ? null : instance);
        }
        throw new RobloxApiException("Este link de convite expirou ou não é válido.");
    }
}
