using System.Security.Cryptography;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Delivery.Forms;
using AgentNotify.Core.Persistence;

namespace AgentNotify.Tests;

public sealed class ProviderFormTests : IAsyncLifetime
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"an-forms-{Guid.NewGuid():N}.db");
    private ProviderProfileService _profiles = null!;

    public async Task InitializeAsync()
    {
        var repository = new SqliteDeliveryRepository(_db);
        await repository.InitializeAsync();
        _profiles = new ProviderProfileService(repository, new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)));
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_db + suffix); } catch { }
        return Task.CompletedTask;
    }

    private const string Sid = "0123456789abcdef0123456789abcdef";

    /// <summary>A complete, valid submission for every kind, as an editor would send it.</summary>
    public static TheoryData<string> Kinds() => new(ProviderFormCatalog.All.Select(k => k.Kind));

    private static ProviderFormInput ValidInput(string kind) => kind switch
    {
        "webhook" => Input(secrets: new() { ["endpoint_url"] = "https://hooks.example.com/in", ["authorization"] = "Bearer abc", ["hmac_secret"] = "k" },
            values: new() { ["allow_private_network"] = "true" }),
        "smtp" => Input(new() { ["host"] = "smtp.example.com", ["port"] = "465", ["security"] = "tls", ["from_address"] = "bot@example.com", ["from_name"] = "Bot", ["recipients"] = "a@example.com\nb@example.com", ["subject_prefix"] = "[x] " },
            new() { ["username"] = "bot", ["password"] = "pw" }),
        "telegram" => Input(new() { ["message_thread_id"] = "42", ["disable_notification"] = "true", ["protect_content"] = "false" },
            new() { ["bot_token"] = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi", ["chat_id"] = "-1001234567890" }),
        "discord" => Input(new() { ["username"] = "Agents", ["thread_id"] = "123456789012345678" }, new() { ["webhook_url"] = "https://discord.com/api/webhooks/1/x" }),
        "slack" => Input(new() { ["thread_timestamp"] = "1712345678.123456" }, new() { ["webhook_url"] = "https://hooks.slack.com/services/x" }),
        "teams" or "zoho_cliq" => Input(secrets: new() { ["webhook_url"] = "https://example.com/hook" }),
        "google_chat" => Input(new() { ["thread_key"] = "builds", ["thread_reply_policy"] = "fail" }, new() { ["webhook_url"] = "https://chat.googleapis.com/v1/spaces/x" }),
        "mattermost" => Input(new() { ["silent"] = "true" }, new() { ["webhook_url"] = "https://mm.example.com/hooks/x" }),
        "matrix" => Input(new() { ["homeserver_base_url"] = "https://matrix.example.org" }, new() { ["access_token"] = "tok", ["room_id"] = "!room:example.org" }),
        "ntfy" => Input(new() { ["server_base_url"] = "https://ntfy.example.com", ["allow_unauthenticated_topic"] = "true" }, new() { ["topic"] = "builds" }),
        "gotify" => Input(new() { ["server_base_url"] = "https://gotify.example.com" }, new() { ["application_token"] = "AbCdEf" }),
        "pushover" => Input(new() { ["sound"] = "siren", ["critical_as_emergency"] = "true", ["emergency_retry_seconds"] = "90", ["emergency_expire_seconds"] = "600" },
            new() { ["application_token"] = new string('a', 30), ["user_key"] = new string('u', 30), ["device"] = "phone" }),
        "pushbullet" => Input(new() { ["target_type"] = "device", ["quota_acknowledged"] = "true" }, new() { ["access_token"] = "o.abcdefghijklmnopqrstu", ["target"] = "ujxyz" }),
        "twilio_sms" => Input(new() { ["credential_mode"] = "api_key", ["sender_type"] = "phone", ["minimum_priority"] = "high", ["validity_period_seconds"] = "120", ["paid_send_consent"] = "true" },
            new() { ["account_sid"] = "AC" + Sid, ["credential_sid"] = "SK" + Sid, ["credential_secret"] = "0123456789abcdefXYZ", ["recipient"] = "+15551234567", ["sender"] = "+15557654321" }),
        "whatsapp_cloud" => Input(new() { ["api_version"] = "v24.0", ["template_name"] = "alert_v2", ["language_code"] = "en", ["body_parameters"] = "title,project", ["minimum_priority"] = "normal",
                ["recipient_opt_in_acknowledged"] = "true", ["template_approved_acknowledged"] = "true", ["paid_send_consent"] = "true" },
            new() { ["phone_number_id"] = "1234567", ["access_token"] = "EAAB0123456789abcdef", ["recipient"] = "+15551234567" }),
        "twilio_whatsapp" => Input(new() { ["credential_mode"] = "auth_token", ["content_variables"] = "message", ["minimum_priority"] = "low", ["validity_period_seconds"] = "600",
                ["recipient_opt_in_acknowledged"] = "true", ["template_approved_acknowledged"] = "true", ["text_only_template_acknowledged"] = "true", ["paid_send_consent"] = "true" },
            new() { ["account_sid"] = "AC" + Sid, ["credential_secret"] = "0123456789abcdefXYZ", ["recipient"] = "+15551234567", ["messaging_service_sid"] = "MG" + Sid, ["content_sid"] = "HX" + Sid }),
        "mqtt" => Input(new() { ["broker_host"] = "MQTT.Example.com", ["port"] = "8884", ["client_id"] = "desk-1", ["authentication_mode"] = "username_password", ["qos"] = "2",
                ["duplicate_risk_acknowledged"] = "true", ["message_expiry_seconds"] = "60", ["allow_private_network"] = "false" },
            new() { ["topic"] = "agents/alerts", ["username"] = "desk", ["password"] = "pw" }),
        "relay" => Input(new() { ["relay_url"] = "https://relay.example.com", ["sender_name"] = "Desk" },
            new() { ["installation_token"] = "inst_abcdefghijklmnop" }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static ProviderFormInput Input(Dictionary<string, string?>? values = null, Dictionary<string, string?>? secrets = null, string[]? clear = null) => new()
    {
        Name = "Test",
        Enabled = true,
        Values = values ?? [],
        Secrets = secrets ?? [],
        ClearSecrets = clear ?? []
    };

    [Fact]
    public void EveryRegisteredAdapterHasAnEditor()
    {
        var adapters = ChannelAdapterFactory.CreateAll().Select(adapter => adapter.Kind).Order().ToArray();
        var editors = ProviderFormCatalog.All.Select(kind => kind.Kind).Order().ToArray();
        Assert.Equal(adapters, editors);
    }

    [Fact]
    public void CatalogKeysAreUniqueAndConditionsPointAtRealFields()
    {
        foreach (var kind in ProviderFormCatalog.All)
        {
            var keys = kind.Fields.Select(field => field.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            foreach (var field in kind.Fields.Where(field => field.ShowWhen is not null))
                Assert.Contains(field.ShowWhen!.Key, keys);
            foreach (var field in kind.Fields.Where(field => field.Type == ProviderFieldType.Select))
                Assert.Contains(field.Default, field.Options!.Select(option => option.Value));
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ReaderReturnsWhatTheBuilderStored(string kind)
    {
        var input = ValidInput(kind);
        var result = ProviderFormBuilder.Build(kind, input, existing: null);
        var profile = new ProviderProfile { Kind = kind, ConfigJson = result.ConfigJson, SecretNames = result.SecretChanges.Keys.ToArray() };

        var values = ProviderFormReader.ReadValues(profile);

        var descriptor = ProviderFormCatalog.Find(kind)!;
        foreach (var field in descriptor.Fields.Where(field => field.Type != ProviderFieldType.Secret && input.Values.ContainsKey(field.Key)))
        {
            var expected = input.Values[field.Key]!;
            if (field.Key == "broker_host") expected = expected.ToLowerInvariant();
            if (field.Key == "recipients") expected = expected.Replace("\r", "");
            Assert.True(values.TryGetValue(field.Key, out var actual), $"{kind}.{field.Key} was not read back");
            Assert.Equal(expected, actual);
        }
        foreach (var (name, value) in input.Secrets.Where(pair => !string.IsNullOrEmpty(pair.Value)))
        {
            if (kind == "twilio_whatsapp" && name == "credential_sid") continue;
            Assert.Equal(value!.Trim(), result.SecretChanges[name]);
        }
        Assert.DoesNotContain(values.Keys, key => descriptor.Fields.Any(field => field.Key == key && field.Type == ProviderFieldType.Secret));
    }

    [Fact]
    public async Task TelegramAdapterDeliversWithTheBuiltConfiguration()
    {
        var input = ValidInput("telegram");
        var result = ProviderFormBuilder.Build("telegram", input, existing: null);
        var sender = new CapturingTelegramSender();
        var adapter = new TelegramChannelAdapter(sender);

        var delivery = await adapter.DeliverAsync(new OutboundDelivery(
            "outbox-1",
            "notification-1",
            "{\"title\":\"Build failed\",\"message\":\"Compiler error\",\"priority\":\"high\",\"type\":\"error\"}",
            new ProviderProfile { Id = "p", Name = "Telegram", Kind = "telegram", Enabled = true, ConfigJson = result.ConfigJson, SecretNames = result.SecretChanges.Keys.ToArray() },
            result.SecretChanges), CancellationToken.None);

        Assert.True(delivery.Succeeded, delivery.ErrorCode);
        var request = Assert.Single(sender.Requests);
        Assert.Equal(42, request.MessageThreadId);
        Assert.True(request.DisableNotification);
        Assert.False(request.ProtectContent);
        Assert.Equal("-1001234567890", request.ChatId);
    }

    [Fact]
    public void ValidationMessagesMatchTheDesktopEditor()
    {
        var missing = Assert.Throws<ArgumentException>(() => ProviderFormBuilder.Build("webhook", Input(), existing: null));
        Assert.Equal("Enter the webhook HTTPS endpoint.", missing.Message);

        var consent = ValidInput("twilio_sms") with { Values = new Dictionary<string, string?>(ValidInput("twilio_sms").Values) { ["paid_send_consent"] = "false" } };
        Assert.Equal("Authorize paid SMS sends before saving this provider.",
            Assert.Throws<ArgumentException>(() => ProviderFormBuilder.Build("twilio_sms", consent, null)).Message);

        var wildcard = ValidInput("mqtt") with { Secrets = new Dictionary<string, string?>(ValidInput("mqtt").Secrets) { ["topic"] = "agents/#" } };
        Assert.Contains("wildcards", Assert.Throws<ArgumentException>(() => ProviderFormBuilder.Build("mqtt", wildcard, null)).Message);

        Assert.Throws<ArgumentException>(() => ProviderFormBuilder.Build("carrier_pigeon", Input(), null));
    }

    [Fact]
    public void DefaultsApplyWhenAFieldIsNotSubmitted()
    {
        var result = ProviderFormBuilder.Build("discord", Input(secrets: new() { ["webhook_url"] = "https://discord.com/api/webhooks/1/x" }), null);
        using var document = JsonDocument.Parse(result.ConfigJson);
        Assert.Equal("AgentNotify", document.RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public void AnInvalidSelectValueIsRefusedRatherThanStoredVerbatim()
    {
        var input = ValidInput("smtp") with { Values = new Dictionary<string, string?>(ValidInput("smtp").Values) { ["security"] = "none" } };
        Assert.Throws<ArgumentException>(() => ProviderFormBuilder.Build("smtp", input, null));
    }

    [Fact]
    public void AProfileCannotChangeKind()
    {
        var existing = new ProviderProfile { Id = "x", Kind = "slack", ConfigJson = "{}", SecretNames = ["webhook_url"] };
        Assert.Throws<ArgumentException>(() => ProviderFormBuilder.Build("discord", ValidInput("discord"), existing));
    }

    [Fact]
    public async Task BlankSecretsKeepStoredValuesAndClearingRemovesThem()
    {
        var forms = new ProviderFormService(_profiles);
        var created = await forms.SaveAsync(null, "webhook", ValidInput("webhook"));
        Assert.Equal(["authorization", "endpoint_url", "hmac_secret"], created.SecretNames);

        var kept = await forms.SaveAsync(created.Id, "webhook", Input(values: new() { ["allow_private_network"] = "false" }) with { Name = "Renamed" });
        Assert.Equal("Renamed", kept.Name);
        Assert.Equal(created.SecretNames, kept.SecretNames);
        Assert.Equal("https://hooks.example.com/in", (await _profiles.GetSecretsForDeliveryAsync(created.Id))["endpoint_url"]);
        using (var config = JsonDocument.Parse(kept.ConfigJson))
            Assert.Equal("authorization", config.RootElement.GetProperty("secretHeaders").GetProperty("Authorization").GetString());

        var cleared = await forms.SaveAsync(created.Id, "webhook", Input(clear: ["authorization"]));
        Assert.Equal(["endpoint_url", "hmac_secret"], cleared.SecretNames);
        using (var config = JsonDocument.Parse(cleared.ConfigJson))
            Assert.Equal(JsonValueKind.Null, config.RootElement.GetProperty("secretHeaders").ValueKind);
    }

    [Fact]
    public async Task SwitchingTwilioToAuthTokenDropsTheStoredApiKeySid()
    {
        var forms = new ProviderFormService(_profiles);
        var created = await forms.SaveAsync(null, "twilio_sms", ValidInput("twilio_sms"));
        Assert.Contains("credential_sid", created.SecretNames);

        var values = new Dictionary<string, string?>(ValidInput("twilio_sms").Values) { ["credential_mode"] = "auth_token" };
        var switched = await forms.SaveAsync(created.Id, "twilio_sms", Input(values));

        Assert.DoesNotContain("credential_sid", switched.SecretNames);
    }

    [Fact]
    public async Task MqttDropsCredentialsTheNewModeDoesNotUse()
    {
        var forms = new ProviderFormService(_profiles);
        var created = await forms.SaveAsync(null, "mqtt", ValidInput("mqtt"));
        var values = new Dictionary<string, string?>(ValidInput("mqtt").Values) { ["authentication_mode"] = "anonymous", ["anonymous_acknowledged"] = "true" };

        var anonymous = await forms.SaveAsync(created.Id, "mqtt", Input(values));

        Assert.Equal(["topic"], anonymous.SecretNames);
    }

    [Fact]
    public async Task RelayPairingSuppliesTheCredentialAndIdentity()
    {
        var forms = new ProviderFormService(_profiles);
        var input = Input(new() { ["relay_url"] = "https://relay.example.com" }) with
        {
            Pairing = new RelayPairingOutcome("inst_pairedcredential01", "installation-7", "Home relay", "install-abc123")
        };

        var saved = await forms.SaveAsync(null, "relay", input);

        Assert.Equal(["installation_token"], saved.SecretNames);
        Assert.Equal("inst_pairedcredential01", (await _profiles.GetSecretsForDeliveryAsync(saved.Id))["installation_token"]);
        Assert.Equal("installation-7", ProviderFormReader.ReadConfigString(saved, "installation_id"));
        Assert.Equal("install-abc123", ProviderFormReader.ReadConfigString(saved, "install_id"));

        // A later save without a new pairing keeps both the credential and the identity.
        var renamed = await forms.SaveAsync(saved.Id, "relay", Input(new() { ["relay_url"] = "https://relay.example.com" }) with { Name = "Phone" });
        Assert.Equal(["installation_token"], renamed.SecretNames);
        Assert.Equal("install-abc123", ProviderFormReader.ReadConfigString(renamed, "install_id"));
    }

    [Fact]
    public void RelayRefusesMissingCredentialAndUnsafeUrl()
    {
        Assert.Contains("Connect", Assert.Throws<ArgumentException>(() =>
            ProviderFormBuilder.Build("relay", Input(new() { ["relay_url"] = "https://relay.example.com" }), null)).Message);

        Assert.Throws<ArgumentException>(() => ProviderFormBuilder.Build("relay",
            Input(new() { ["relay_url"] = "http://relay.example.com" }, new() { ["installation_token"] = "inst_abcdefghijklmnop" }), null));
    }

    [Fact]
    public void AMalformedStoredDocumentStillOpens()
    {
        var values = ProviderFormReader.ReadValues(new ProviderProfile { Kind = "pushover", ConfigJson = "{not json" });
        Assert.Equal("60", values["emergency_retry_seconds"]);
    }

    private sealed class CapturingTelegramSender : ITelegramSender
    {
        public List<TelegramSendRequest> Requests { get; } = [];

        public Task<DeliveryResult> SendAsync(TelegramSendRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(DeliveryResult.Success(200));
        }
    }
}
