using System.Security.Cryptography;

namespace AgentNotify.Core.Services;

public sealed class ManagedSoundStore
{
    public const long MaxSoundBytes = 10 * 1024 * 1024;
    public string DirectoryPath { get; }

    public ManagedSoundStore(string directoryPath) => DirectoryPath = directoryPath;

    public string Import(string sourcePath)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists) throw new FileNotFoundException("Sound file was not found.", sourcePath);
        if (source.Length is <= 0 or > MaxSoundBytes) throw new InvalidOperationException("Sound files must be between 1 byte and 10 MB.");
        var extension = source.Extension.ToLowerInvariant();
        if (extension is not ".wav" and not ".mp3") throw new InvalidOperationException("Only WAV and MP3 sound files are supported.");
        Directory.CreateDirectory(DirectoryPath);
        using var stream = source.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()[..16];
        var safeBase = string.Concat(Path.GetFileNameWithoutExtension(source.Name).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
        if (safeBase.Length == 0) safeBase = "sound";
        if (safeBase.Length > 40) safeBase = safeBase[..40];
        var fileName = $"{safeBase}-{hash}{extension}";
        var destination = Path.Combine(DirectoryPath, fileName);
        if (!File.Exists(destination)) File.Copy(source.FullName, destination);
        return fileName;
    }

    public string? Resolve(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var path = Path.Combine(DirectoryPath, Path.GetFileName(fileName));
        return File.Exists(path) ? path : null;
    }
}

