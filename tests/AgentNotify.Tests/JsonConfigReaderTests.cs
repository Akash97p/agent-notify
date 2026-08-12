using System.Text.Json;
using AgentNotify.Core;

namespace AgentNotify.Tests;

/// <summary>
/// Optional provider settings are serialized as JSON null when the user leaves them blank, and
/// reading one back used to throw and terminate the Settings window. These pin the safe behaviour.
/// </summary>
public sealed class JsonConfigReaderTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TryGetInt32_TreatsJsonNullAsNotSet()
    {
        // This is exactly what a Telegram provider with no topic ID stores. JsonElement.TryGetInt32
        // throws InvalidOperationException here, which is what crashed the tray process.
        var root = Parse("""{"messageThreadId":null}""");

        Assert.False(JsonConfigReader.TryGetInt32(root, "messageThreadId", out var value));
        Assert.Equal(0, value);
        Assert.Equal("", JsonConfigReader.GetInt32Text(root, "messageThreadId"));
    }

    [Theory]
    [InlineData("""{"port":null}""")]
    [InlineData("""{"port":"587"}""")]
    [InlineData("""{"port":true}""")]
    [InlineData("""{"port":{}}""")]
    [InlineData("""{"port":[]}""")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{}""")]
    public void TryGetInt32_NeverThrowsOnUnexpectedShapes(string json)
    {
        Assert.False(JsonConfigReader.TryGetInt32(Parse(json), "port", out _));
    }

    [Fact]
    public void JsonElementTryGetInt32_ThrowsOnNull_WhichIsWhyThisHelperExists()
    {
        var root = Parse("""{"messageThreadId":null}""");
        Assert.True(root.TryGetProperty("messageThreadId", out var element));

        // Documents the trap: despite the Try prefix, this throws rather than returning false for
        // any kind that is not a number. If a future runtime softens that, this test tells us.
        Assert.Throws<InvalidOperationException>(() => element.TryGetInt32(out _));
    }

    [Fact]
    public void TryGetInt32_ReadsAnActualNumber()
    {
        var root = Parse("""{"messageThreadId":42,"port":587}""");

        Assert.True(JsonConfigReader.TryGetInt32(root, "messageThreadId", out var thread));
        Assert.Equal(42, thread);
        Assert.Equal("587", JsonConfigReader.GetInt32Text(root, "port"));
    }

    [Fact]
    public void TryGetInt32_RejectsNumbersThatDoNotFit()
    {
        var root = Parse("""{"port":99999999999999999999,"fraction":1.5}""");

        Assert.False(JsonConfigReader.TryGetInt32(root, "port", out _));
        Assert.False(JsonConfigReader.TryGetInt32(root, "fraction", out _));
    }

    [Fact]
    public void GetString_TreatsNullAndWrongTypesAsEmpty()
    {
        var root = Parse("""{"host":null,"port":587,"name":"smtp.example.com"}""");

        Assert.Equal("", JsonConfigReader.GetString(root, "host"));
        Assert.Equal("", JsonConfigReader.GetString(root, "port"));
        Assert.Equal("", JsonConfigReader.GetString(root, "missing"));
        Assert.Equal("smtp.example.com", JsonConfigReader.GetString(root, "name"));
    }

    [Fact]
    public void GetBoolean_FallsBackForNullAndMissingValues()
    {
        var root = Parse("""{"protectContent":null,"disableNotification":false,"enabled":true}""");

        Assert.True(JsonConfigReader.GetBoolean(root, "protectContent", fallback: true));
        Assert.False(JsonConfigReader.GetBoolean(root, "disableNotification", fallback: true));
        Assert.True(JsonConfigReader.GetBoolean(root, "enabled", fallback: false));
        Assert.True(JsonConfigReader.GetBoolean(root, "missing", fallback: true));
    }

    [Fact]
    public void GetStringArray_SkipsEntriesThatAreNotStrings()
    {
        var root = Parse("""{"recipients":["a@example.com",null,7,"b@example.com"],"none":null}""");

        Assert.Equal(["a@example.com", "b@example.com"], JsonConfigReader.GetStringArray(root, "recipients"));
        Assert.Empty(JsonConfigReader.GetStringArray(root, "none"));
        Assert.Empty(JsonConfigReader.GetStringArray(root, "missing"));
    }

    [Fact]
    public void Readers_TolerateANonObjectDocument()
    {
        var root = Parse("[1,2,3]");

        Assert.False(JsonConfigReader.TryGetInt32(root, "port", out _));
        Assert.Equal("", JsonConfigReader.GetString(root, "host"));
        Assert.True(JsonConfigReader.GetBoolean(root, "enabled", fallback: true));
        Assert.Empty(JsonConfigReader.GetStringArray(root, "recipients"));
    }

    [Fact]
    public void TheStoredTelegramConfigurationLoadsWithoutThrowing()
    {
        // Byte-for-byte the shape written by the Settings window for a Telegram provider with no
        // topic ID, which is what the crash was reproduced from.
        var root = Parse(
            """
            {"botTokenSecretName":"bot_token","chatIdSecretName":"chat_id",
             "messageThreadId":null,"disableNotification":false,"protectContent":true}
            """);

        Assert.Equal("", JsonConfigReader.GetInt32Text(root, "messageThreadId"));
        Assert.False(JsonConfigReader.GetBoolean(root, "disableNotification", fallback: false));
        Assert.True(JsonConfigReader.GetBoolean(root, "protectContent", fallback: true));
    }
}
