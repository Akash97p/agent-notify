using System.Text.RegularExpressions;

namespace AgentNotify.Contracts;

/// <summary>Stable notification type identifiers used by the API and persistence.</summary>
public static partial class NotificationTypes
{
    public const string Info = "info";
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string InputRequired = "input_required";
    public const string PermissionRequired = "permission_required";
    public const string Completed = "completed";
    public const string Blocked = "blocked";

    public static IReadOnlyList<string> BuiltIns { get; } =
        [Info, Success, Warning, Error, InputRequired, PermissionRequired, Completed, Blocked];

    public static string FromBuiltIn(NotificationType type) => type switch
    {
        NotificationType.Success => Success,
        NotificationType.Warning => Warning,
        NotificationType.Error => Error,
        NotificationType.InputRequired => InputRequired,
        NotificationType.PermissionRequired => PermissionRequired,
        NotificationType.Completed => Completed,
        NotificationType.Blocked => Blocked,
        _ => Info
    };

    public static bool IsValid(string? value) => value is not null && TypeIdRegex().IsMatch(value);

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace('-', '_').ToLowerInvariant();
        normalized = normalized switch
        {
            "inputrequired" => InputRequired,
            "permissionrequired" => PermissionRequired,
            _ => normalized
        };
        return IsValid(normalized) ? normalized : null;
    }

    public static bool IsAttention(string? value) => value is InputRequired or PermissionRequired or Blocked or Error;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeIdRegex();
}
