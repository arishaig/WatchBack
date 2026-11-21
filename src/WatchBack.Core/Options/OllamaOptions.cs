namespace WatchBack.Core.Options;

public class OllamaOptions
{
    public string BaseUrl { get; init; } = "http://ollama:11434";
    public string Model { get; init; } = "llama3.2";
    public string ApiKey { get; init; } = string.Empty;
}
