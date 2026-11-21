namespace WatchBack.Core.Options;

public class LibreTranslateOptions
{
    public string BaseUrl { get; init; } = "http://libretranslate:5000";
    public string ApiKey { get; init; } = string.Empty;
}
