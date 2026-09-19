using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Router;
using AgentNotify.Router.Connect;
using AgentNotify.Router.Translation;
using AgentNotify.Api.WebUi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.Router;

public static partial class RouterEndpoints
{
    private sealed class UpstreamBody
    {
        public string? Slug { get; set; }
        public string? Label { get; set; }
        public string? Wire { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public bool? ClearKey { get; set; }
        public IReadOnlyList<string>? Models { get; set; }
        public bool? Enabled { get; set; }
        public bool? AckKeyStorage { get; set; }
        public bool? AcknowledgeRisk { get; set; }
        public string? Auth { get; set; }
        public string? PresetId { get; set; }
        public string? UpstreamId { get; set; }
        public bool? UseOpencodeKey { get; set; }
        public Dictionary<string, string>? ModelWires { get; set; }
        public string? CredentialRef { get; set; }
    }
    private sealed class RouteBody { public string? Name { get; set; } public string? Kind { get; set; } public IReadOnlyList<string>? Targets { get; set; } public bool? Enabled { get; set; } }
    private sealed class DefaultRouteBody { public string? Route { get; set; } }
    private sealed class SwitchSettingsBody
    {
        public string? Strategy { get; set; }
        public string? ClaudeFallbackRoute { get; set; }
    }
    private sealed class EffortMappingBody
    {
        public string? UpstreamId { get; set; }
        public string? Model { get; set; }
        public string? Family { get; set; }
        public IReadOnlyList<string>? SupportedValues { get; set; }
        public IReadOnlyList<string>? LevelMap { get; set; }
        public string? DefaultValue { get; set; }
    }
}
