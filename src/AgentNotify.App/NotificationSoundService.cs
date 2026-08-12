using System.IO;
using System.Windows.Media;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Services;

namespace AgentNotify.App;

public sealed class NotificationSoundService : IDisposable
{
    private readonly AgentNotifyConfig _config;
    private readonly IAppLogger _logger;
    private readonly ManagedSoundStore _store;
    private readonly List<MediaPlayer> _players = [];

    public NotificationSoundService(AgentNotifyConfig config, string soundsDir, IAppLogger logger)
    {
        _config = config; _logger = logger; _store = new ManagedSoundStore(soundsDir);
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var tone in BuiltInTones.All)
            {
                var resourceName = $"AgentNotify.Resources.Tones.{tone.FileName}";
                var probe = assembly.GetManifestResourceStream(resourceName);
                if (probe is null)
                {
                    _logger.Info($"Built-in tone resource missing: {resourceName}");
                    continue;
                }
                probe.Dispose();
                _store.SeedBuiltIn(tone.FileName, () => assembly.GetManifestResourceStream(resourceName)!);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to seed built-in tones", ex);
        }
    }

    public string Import(string sourcePath)
    {
        return _store.Import(sourcePath);
    }

    public void PlayFor(string type, NotificationPriority priority)
    {
        if (!NotificationSoundPolicy.ShouldPlay(_config, priority)) return;
        var file = _config.SoundFileFor(type);
        if (file is not null) Play(file);
    }

    public void Preview(string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName)) Play(Path.GetFileName(fileName));
    }

    private void Play(string fileName)
    {
        try
        {
            var path = _store.Resolve(fileName);
            if (path is null) { _logger.Info($"Configured sound file is missing: {Path.GetFileName(fileName)}"); return; }
            var player = new MediaPlayer { Volume = Math.Clamp(_config.SoundVolume, 0, 1) };
            _players.Add(player);
            void Done(object? _, EventArgs __) { player.Close(); _players.Remove(player); }
            player.MediaEnded += Done;
            player.MediaFailed += (_, e) => { _logger.Error($"Could not play sound {Path.GetFileName(fileName)}", e.ErrorException); Done(null, EventArgs.Empty); };
            player.Open(new Uri(path, UriKind.Absolute));
            player.Play();
        }
        catch (Exception ex) { _logger.Error("Notification sound playback failed", ex); }
    }

    public void Dispose()
    {
        foreach (var player in _players.ToArray()) player.Close();
        _players.Clear();
    }
}
