using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Media.Imaging;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;

namespace AgentNotify.App;

/// <summary>Accent color per notification type (used by toasts, center, and icons).</summary>
public static class TypeVisuals
{
    public static Color ColorFor(string type) => NotificationTypes.Normalize(type) switch
    {
        NotificationTypes.Success => Color.FromArgb(0x2E, 0x9E, 0x5B),
        NotificationTypes.Warning => Color.FromArgb(0xD9, 0x9A, 0x2B),
        NotificationTypes.Error => Color.FromArgb(0xD6, 0x45, 0x45),
        NotificationTypes.InputRequired => Color.FromArgb(0x8B, 0x5C, 0xF6),
        NotificationTypes.PermissionRequired => Color.FromArgb(0x7C, 0x3A, 0xED),
        NotificationTypes.Completed => Color.FromArgb(0x0E, 0x9F, 0x6E),
        NotificationTypes.Blocked => Color.FromArgb(0xB9, 0x1C, 0x1C),
        _ => Color.FromArgb(0x4A, 0x90, 0xD9)
    };

    public static System.Windows.Media.Color WpfColorFor(string type, AgentNotifyConfig? config = null)
    {
        var custom = config?.CustomType(type);
        if (custom is not null)
        {
            var parsed = ColorTranslator.FromHtml(custom.AccentColor);
            return System.Windows.Media.Color.FromRgb(parsed.R, parsed.G, parsed.B);
        }
        var c = ColorFor(type);
        return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
    }

    public static string LabelFor(string type, AgentNotifyConfig? config = null)
    {
        var custom = config?.CustomType(type);
        if (custom is not null) return custom.DisplayName;
        return NotificationTypes.Normalize(type) switch
    {
        NotificationTypes.Success => "Done", NotificationTypes.Warning => "Heads up",
        NotificationTypes.Error => "Error", NotificationTypes.InputRequired => "Input needed",
        NotificationTypes.PermissionRequired => "Permission needed", NotificationTypes.Completed => "Completed",
        NotificationTypes.Blocked => "Blocked", NotificationTypes.Info => "Info",
        _ => type.Replace('_', ' ')
        };
    }
}

/// <summary>Loads the branded multi-resolution icon embedded in the WPF application.</summary>
public static class AppIcons
{
    public static Icon CreateTrayIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/an.ico"));
        if (info is null)
            throw new InvalidOperationException("The AgentNotify icon resource is missing.");
        using var stream = info.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    public static System.Windows.Media.ImageSource CreateWindowIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/an.ico"));
        if (info is null)
            throw new InvalidOperationException("The AgentNotify icon resource is missing.");
        using var stream = info.Stream;
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
