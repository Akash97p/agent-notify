using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Media.Imaging;
using AgentNotify.Contracts;

namespace AgentNotify.App;

/// <summary>Accent color per notification type (used by toasts, center, and icons).</summary>
public static class TypeVisuals
{
    public static Color ColorFor(NotificationType type) => type switch
    {
        NotificationType.Success => Color.FromArgb(0x2E, 0x9E, 0x5B),
        NotificationType.Warning => Color.FromArgb(0xD9, 0x9A, 0x2B),
        NotificationType.Error => Color.FromArgb(0xD6, 0x45, 0x45),
        NotificationType.InputRequired => Color.FromArgb(0x8B, 0x5C, 0xF6),
        NotificationType.PermissionRequired => Color.FromArgb(0x7C, 0x3A, 0xED),
        NotificationType.Completed => Color.FromArgb(0x0E, 0x9F, 0x6E),
        NotificationType.Blocked => Color.FromArgb(0xB9, 0x1C, 0x1C),
        _ => Color.FromArgb(0x4A, 0x90, 0xD9)
    };

    public static System.Windows.Media.Color WpfColorFor(NotificationType type)
    {
        var c = ColorFor(type);
        return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
    }

    public static string LabelFor(NotificationType type) => type switch
    {
        NotificationType.Success => "Done",
        NotificationType.Warning => "Heads up",
        NotificationType.Error => "Error",
        NotificationType.InputRequired => "Input needed",
        NotificationType.PermissionRequired => "Permission needed",
        NotificationType.Completed => "Completed",
        NotificationType.Blocked => "Blocked",
        _ => "Info"
    };
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
