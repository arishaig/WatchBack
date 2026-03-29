using System.Collections;
using System.Globalization;
using System.Resources;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using WatchBack.Resources;

namespace WatchBack.Api.Endpoints;

public static partial class StringsEndpoints
{
    private static readonly ResourceManager s_uiStringsManager =
        new("WatchBack.Resources.UiStrings", typeof(UiStrings).Assembly);

    private static readonly ResourceManager s_frontendStringsManager =
        new("WatchBack.Resources.FrontendStrings", typeof(UiStrings).Assembly);

    public static void MapStringsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api")
            .WithTags("Localization");

        group.MapGet("/strings", GetStrings)
            .WithName("GetStrings")
            .WithSummary("Get localized UI strings")
            .WithDescription(
                "Returns all UI and frontend strings for the current Accept-Language culture. Strings may contain {0}, {1} placeholders for interpolation.")
            .Produces(StatusCodes.Status200OK)
            .AllowAnonymous();

        group.MapGet("/strings/all", GetAllStrings)
            .WithName("GetAllStrings")
            .WithSummary("Get all localized UI strings for every supported locale")
            .WithDescription(
                "Returns strings for all supported cultures at once, enabling instant client-side locale switching.")
            .Produces(StatusCodes.Status200OK)
            .AllowAnonymous();
    }

    private static Dictionary<string, string> GetStrings([FromServices] ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger(nameof(StringsEndpoints));
        CultureInfo culture = CultureInfo.CurrentUICulture;
        Dictionary<string, string> strings = new(StringComparer.Ordinal);

        try
        {
            LoadResourceStrings(s_uiStringsManager, culture, strings, "UiStrings", logger);
            LoadResourceStrings(s_frontendStringsManager, culture, strings, "FrontendStrings", logger);
        }
        catch (Exception ex)
        {
            LogCultureLoadError(logger, culture.Name, ex);
            throw;
        }

        return strings;
    }

    private static object GetAllStrings(
        [FromServices] IOptions<RequestLocalizationOptions> locOptions,
        [FromServices] ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger(nameof(StringsEndpoints));
        IList<CultureInfo> cultures = locOptions.Value.SupportedUICultures ?? [new CultureInfo("en")];
        List<string> supportedLocales = new();
        Dictionary<string, Dictionary<string, string>> allStrings = new(StringComparer.Ordinal);

        foreach (CultureInfo culture in cultures)
        {
            // Use the two-letter language code as the key so the frontend can match
            // navigator.language ("en", "es") without worrying about region suffixes.
            string locale = culture.TwoLetterISOLanguageName;
            if (supportedLocales.Contains(locale))
            {
                continue;
            }

            supportedLocales.Add(locale);
            Dictionary<string, string> strings = new(StringComparer.Ordinal);
            try
            {
                LoadResourceStrings(s_uiStringsManager, culture, strings, "UiStrings", logger);
                LoadResourceStrings(s_frontendStringsManager, culture, strings, "FrontendStrings", logger);
            }
            catch (Exception ex)
            {
                LogCultureLoadError(logger, culture.Name, ex);
            }

            allStrings[locale] = strings;
        }

        return new { supportedLocales, strings = allStrings };
    }

    private static void LoadResourceStrings(ResourceManager manager, CultureInfo culture,
        Dictionary<string, string> strings, string resourceName, ILogger logger)
    {
        try
        {
            ResourceSet? resourceSet = manager.GetResourceSet(culture, true, true);
            if (resourceSet != null)
            {
                foreach (DictionaryEntry entry in resourceSet)
                {
                    string? key = entry.Key as string;
                    string? value = entry.Value as string;
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    {
                        strings[key] = value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogResourceLoadError(logger, resourceName, culture.Name, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error loading localized strings for culture {Culture}")]
    private static partial void LogCultureLoadError(ILogger logger, string culture, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error loading {ResourceName} strings for culture {Culture}")]
    private static partial void LogResourceLoadError(ILogger logger, string resourceName, string culture, Exception ex);
}
