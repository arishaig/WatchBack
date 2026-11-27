namespace WatchBack.Core.Options;

/// <summary>Empty until the user sets Ollama up, so "has a language model" means they did.</summary>
public class OllamaOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
}
