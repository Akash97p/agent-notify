namespace AgentNotify.Contracts;

public sealed class HealthResponse
{
    public string Status { get; set; } = "";
    public string Version { get; set; } = "";
    public int Pid { get; set; }
    public double UptimeSeconds { get; set; }
    public int ActiveCount { get; set; }
    public string ApiVersion { get; set; } = "v1";
    public DateTimeOffset ServerTimeUtc { get; set; }
}
