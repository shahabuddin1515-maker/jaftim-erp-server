using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hangfire.Dashboard;

namespace Jaftim.Jobs;

/// <summary>
/// HTTP Basic auth for /hangfire. Credentials come from configuration (Hangfire:Dashboard:Username/Password), which
/// in UAT/Prod means Key Vault or App Service settings. Put the host behind the same IP restrictions as the API.
/// </summary>
public sealed class DashboardAuthorizationFilter(string username, string password) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        HttpContext http = context.GetHttpContext();
        string? header = http.Request.Headers.Authorization.FirstOrDefault();

        if (header is not null
            && AuthenticationHeaderValue.TryParse(header, out AuthenticationHeaderValue? value)
            && value.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            && value.Parameter is not null)
        {
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(value.Parameter)).Split(':', 2);
            if (parts.Length == 2 && FixedEquals(parts[0], username) && FixedEquals(parts[1], password))
                return true;
        }

        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.Headers.WWWAuthenticate = "Basic realm=\"Jaftim Jobs\"";
        return false;
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
