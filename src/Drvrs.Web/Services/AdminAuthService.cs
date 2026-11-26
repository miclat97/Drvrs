using System.Net;
using System.Security.Cryptography;
using System.Text;
using Drvrs.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Drvrs.Web.Services;

/// <summary>
/// Provides constant-time password verification, ephemeral HMAC-signed HttpOnly session cookies,
/// and Cloudflare Tunnel aware client IP resolution.
/// </summary>
public sealed class AdminAuthService(IOptions<DrvrsOptions> options)
{
    public const string CookieName = "drvrs_admin_auth";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);

    // Ephemeral 256-bit key generated per process start — restarting the server invalidates all previous cookies.
    private readonly byte[] _ephemeralSigningKey = RandomNumberGenerator.GetBytes(32);

    public bool IsAdminConfiguredAndEnabled
    {
        get
        {
            var opts = options.Value;
            return opts.EnableAdminEndpoints && !string.IsNullOrWhiteSpace(opts.AdminToken);
        }
    }

    public bool VerifyPasswordConstantTime(string? candidatePassword)
    {
        if (!IsAdminConfiguredAndEnabled || string.IsNullOrEmpty(candidatePassword))
        {
            return false;
        }

        var expected = options.Value.AdminToken!;
        // Hash both inputs with SHA-256 before FixedTimeEquals so password length is never leaked via timing
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidatePassword));
        return CryptographicOperations.FixedTimeEquals(expectedHash, candidateHash);
    }

    public bool IsRequestAuthorized(HttpContext context)
    {
        if (!IsAdminConfiguredAndEnabled)
        {
            return false;
        }

        // 1. Check X-Admin-Token header (for scripts / curl)
        var headerToken = context.Request.Headers["X-Admin-Token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(headerToken) && VerifyPasswordConstantTime(headerToken))
        {
            return true;
        }

        // 2. Check signed HttpOnly cookie
        if (context.Request.Cookies.TryGetValue(CookieName, out var cookieValue) &&
            !string.IsNullOrWhiteSpace(cookieValue))
        {
            return ValidateSignedToken(cookieValue);
        }

        return false;
    }

    public void IssueAdminCookie(HttpContext context)
    {
        var expiresAtUnix = DateTimeOffset.UtcNow.Add(CookieLifetime).ToUnixTimeSeconds();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var payload = $"{expiresAtUnix}:{nonce}";
        var sig = ComputeHmacHex(payload);
        var token = $"{payload}:{sig}";

        var isHttps = context.Request.IsHttps ||
                      string.Equals(context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase);

        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api/admin",
            MaxAge = CookieLifetime,
            IsEssential = true
        });
    }

    public void ClearAdminCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/admin"
        });
    }

    /// <summary>
    /// Resolves real visitor IP when running behind local cloudflared / Nginx reverse proxy,
    /// without trusting spoofed headers from direct non-private connections.
    /// </summary>
    public static string ResolveClientIpKey(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null || IsLoopbackOrPrivate(remoteIp))
        {
            var cfIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(cfIp) && IPAddress.TryParse(cfIp.Trim(), out var parsedCf))
            {
                return parsedCf.ToString();
            }

            var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(xff))
            {
                var firstHop = xff.Split(',')[0].Trim();
                if (IPAddress.TryParse(firstHop, out var parsedXff))
                {
                    return parsedXff.ToString();
                }
            }
        }

        return remoteIp?.ToString() ?? "unknown";
    }

    private bool ValidateSignedToken(string token)
    {
        var parts = token.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!long.TryParse(parts[0], out var expiresAtUnix))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix)
        {
            return false;
        }

        var payload = $"{parts[0]}:{parts[1]}";
        var expectedSigBytes = Encoding.ASCII.GetBytes(ComputeHmacHex(payload));
        var actualSigBytes = Encoding.ASCII.GetBytes(parts[2]);

        return expectedSigBytes.Length == actualSigBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedSigBytes, actualSigBytes);
    }

    private string ComputeHmacHex(string payload)
    {
        var mac = HMACSHA256.HashData(_ephemeralSigningKey, Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(mac);
    }

    private static bool IsLoopbackOrPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168);
        }

        return false;
    }
}
