using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Core;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Delivery.Forms;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Harness;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Persistence;
using AgentNotify.Insights.Quota;
using AgentNotify.Core.Services;
using AgentNotify.Core.Skills;
using AgentNotify.Insights.Usage;
using AgentNotify.Core.Wsl;
using AgentNotify.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.WebUi;

/// <summary>Web UI routes: settings.</summary>
public sealed partial class WebUiEndpoints
{
    private void MapSettings()
    {
        app.MapGet($"{BasePath}/api/settings", () => Results.Json(SettingsJson(config), JsonOptions));

        app.MapPut($"{BasePath}/api/settings", async (HttpContext http) =>
        {
            var body = await ReadAsync<SettingsBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                var originalPort = config.Port;
                ApplySettings(config, body);
                options.ConfigStore.Save(config);
                var restart = config.Port != originalPort;
                Notify(options, config, restart, logger);
                return Results.Json(new { settings = SettingsJson(config), restart_required = restart }, JsonOptions);
            }
            catch (ArgumentException exception)
            {
                return Error(exception.Message);
            }
        });

        app.MapGet($"{BasePath}/api/types", () => Results.Json(TypesJson(config), JsonOptions));

        app.MapPut($"{BasePath}/api/types", async (HttpContext http) =>
        {
            var body = await ReadAsync<TypesBody>(http);
            if (body?.Custom is null) return Error("The request body is not valid JSON.");
            try
            {
                var definitions = ValidateTypes(body.Custom);
                var kept = definitions.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var removed in config.CustomNotificationTypes.Select(d => d.Id).Where(id => !kept.Contains(id)).ToList())
                    config.TypeSoundFiles.Remove(removed);
                config.CustomNotificationTypes = definitions;
                options.ConfigStore.Save(config);
                Notify(options, config, false, logger);
                return Results.Json(TypesJson(config), JsonOptions);
            }
            catch (ArgumentException exception)
            {
                return Error(exception.Message);
            }
        });

        // ---- sounds ------------------------------------------------------------------------

        app.MapGet($"{BasePath}/api/sounds", () =>
        {
            var builtIn = BuiltInTones.All.Select(t => new { file_name = t.FileName, display_name = t.DisplayName, available = sounds.Resolve(t.FileName) is not null });
            var imported = Directory.Exists(sounds.DirectoryPath)
                ? Directory.EnumerateFiles(sounds.DirectoryPath)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null && !BuiltInTones.Contains(name) &&
                                   (name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            return Results.Json(new { built_in = builtIn, imported }, JsonOptions);
        });

        app.MapPost($"{BasePath}/api/sounds", async (HttpContext http) =>
        {
            var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MaximumSoundUploadBytes;
            if (!http.Request.HasFormContentType) return Error("Upload a WAV or MP3 file.");

            IFormFile? file;
            try
            {
                file = (await http.Request.ReadFormAsync(http.RequestAborted)).Files.GetFile("file");
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException)
            {
                return Error("Sound files must be at most 10 MB.");
            }
            if (file is null || file.Length == 0) return Error("Upload a WAV or MP3 file.");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is not ".wav" and not ".mp3") return Error("Only WAV and MP3 sound files are supported.");
            var baseName = Path.GetFileNameWithoutExtension(SafeFileName.Last(file.FileName));
            var staging = Path.Combine(Path.GetTempPath(), $"agentnotify-upload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var temporary = Path.Combine(staging, (string.IsNullOrWhiteSpace(baseName) ? "sound" : baseName) + extension);
            try
            {
                await using (var target = File.Create(temporary))
                    await file.CopyToAsync(target, http.RequestAborted);
                return Results.Json(new { file_name = sounds.Import(temporary) }, JsonOptions);
            }
            catch (InvalidOperationException exception)
            {
                return Error(exception.Message);
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { }
            }
        });

        app.MapGet($"{BasePath}/api/sounds/{{fileName}}", (string fileName) =>
        {
            var path = sounds.Resolve(fileName);
            if (path is null) return Error("That sound is not available on this machine.", StatusCodes.Status404NotFound);
            var type = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav";
            return Results.File(path, type);
        });
    }
}
