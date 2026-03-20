using System.Globalization;
using System.Resources;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace WatchBack.Api.Endpoints;

public static class StringsEndpoints
{
    private static readonly ResourceManager s_uiStringsManager = new("WatchBack.Resources.UiStrings", typeof(WatchBack.Resources.UiStrings).Assembly);
    private static readonly ResourceManager s_frontendStringsManager = new("WatchBack.Resources.FrontendStrings", typeof(WatchBack.Resources.UiStrings).Assembly);

    public static void MapStringsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .WithTags("Localization");

        group.MapGet("/strings", GetStrings)
            .WithName("GetStrings")
            .WithSummary("Get localized UI strings")
            .WithDescription("Returns all UI and frontend strings for the current Accept-Language culture. Strings may contain {0}, {1} placeholders for interpolation.")
            .Produces(StatusCodes.Status200OK)
            .AllowAnonymous();
    }

    private static object GetStrings([FromServices] ILogger<object> logger)
    {
        var culture = CultureInfo.CurrentUICulture;
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            LoadResourceStrings(s_uiStringsManager, culture, strings, "UiStrings", logger);
            LoadResourceStrings(s_frontendStringsManager, culture, strings, "FrontendStrings", logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading localized strings for culture {Culture}", culture.Name);
            throw;
        }

        return strings;
    }

    private static void LoadResourceStrings(ResourceManager manager, CultureInfo culture, Dictionary<string, string> strings, string resourceName, ILogger logger)
    {
        try
        {
            var resourceSet = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: true);
            if (resourceSet != null)
            {
                foreach (System.Collections.DictionaryEntry entry in resourceSet)
                {
                    var key = entry.Key as string;
                    var value = entry.Value as string;
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    {
                        strings[key] = value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading {ResourceName} strings for culture {Culture}", resourceName, culture.Name);
        }
    }
}
