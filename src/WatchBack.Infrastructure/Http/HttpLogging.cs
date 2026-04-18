using System.Net.Http.Headers;

namespace WatchBack.Infrastructure.Http;

/// <summary>
///     Shared HTTP logging helpers used by the resilience pipeline.
/// </summary>
internal static class HttpLogging
{
    /// <summary>
    ///     Returns a log-safe URI string with the values of sensitive query parameters
    ///     (apikey, api_key, key, token, secret) replaced by <c>***</c>.
    /// </summary>
    internal static string RedactSensitiveParams(Uri? uri)
    {
        if (uri == null)
        {
            return "(no uri)";
        }

        string query = uri.Query;
        if (string.IsNullOrEmpty(query))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }

        System.Text.StringBuilder sb = new(uri.GetLeftPart(UriPartial.Path));
        sb.Append('?');

        bool first = true;
        foreach (string part in query.TrimStart('?').Split('&'))
        {
            int eq = part.IndexOf('=');
            string paramName = eq >= 0 ? part[..eq] : part;
            string paramValue = eq >= 0 ? part[(eq + 1)..] : string.Empty;

            if (!first)
            {
                sb.Append('&');
            }

            first = false;

            if (IsSensitiveParam(paramName))
            {
                sb.Append(paramName).Append("=***");
            }
            else
            {
                sb.Append(part);
            }
        }

        return sb.ToString();
    }

    private static bool IsSensitiveParam(string name)
    {
        return name.Equals("apikey", StringComparison.OrdinalIgnoreCase)
               || name.Equals("api_key", StringComparison.OrdinalIgnoreCase)
               || name.Equals("key", StringComparison.OrdinalIgnoreCase)
               || name.Equals("token", StringComparison.OrdinalIgnoreCase)
               || name.Equals("secret", StringComparison.OrdinalIgnoreCase);
    }
}
