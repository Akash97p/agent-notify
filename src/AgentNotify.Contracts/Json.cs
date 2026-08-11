using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentNotify.Contracts;

/// <summary>Shared JSON options so the API and CLI agree on formatting.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
