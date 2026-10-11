using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests;

public sealed class BrowserPageAutoProtocolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CacheProofReachesFrontendSoReplaysNeedNoArtificialTyping(bool cached)
    {
        using var json = JsonDocument.Parse(WireCandidate);
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        using var reply = JsonDocument.Parse(BrowserPageAutoProtocol.Serialize(candidate!, new("pageAutoTranslations",
            Updates: [new("p-1", "译文", true, cached)])));
        var update = Assert.Single(reply.RootElement.GetProperty("updates").EnumerateArray());
        Assert.Equal(cached, update.GetProperty("fromCache").GetBoolean()); Assert.True(update.GetProperty("complete").GetBoolean());
    }
    private static readonly Guid Connection = Guid.Parse("c2fc5587-7eca-4af5-aa5e-d9f2da88b27a");
    private const string SourceGroupBlockText = "Ready 😀 for translation";
    private const string SourceGroupText = "😀 Prefix: " + SourceGroupBlockText + " Unsent sibling.";
    private static readonly string[] SourceGroupFields = ["sourceGroupHash", "sourceGroupLength", "sourceStart", "sourceLength"];
    private const string WireCandidate = """
        {
          "type":"pageAutoCandidate","requestId":"batch-1","documentToken":"doc-123",
          "tabId":4,"browserWindowId":9,"frameId":0,"generation":100,"revision":2,"documentLanguage":"en",
          "blocks":[{"id":"p-1","text":"This paragraph is public and has enough English words to translate.","groupId":"g-1"}],
          "samples":[{"text":"This paragraph is public and has enough English words to translate.","kind":"body"}],
          "pageSummary":{"totalCharacters":300,"hanCharacters":40,"kanaCharacters":0,"latinCharacters":180,
            "proseBlocks":3,"hanProseCharacters":40,"latinProseCharacters":180,"kanaProseCharacters":0}
        }
        """;

    [Fact]
    public void WholePageStatisticsUseTheActualExtensionFieldsAndStopMixedLanguageUpload()
    {
        using var json = JsonDocument.Parse(WireCandidate);
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Equal(new PageTextStatistics(300, 40, 0, 180, 3, 40, 180, 0), candidate!.Statistics);
        var decision = PageTranslationDecision.Inspect(candidate.Samples!, "zh-CN", candidate.Statistics).RuleDecision;
        Assert.False(decision.NeedsTranslation);
        Assert.True(decision.IsMixedTargetLanguage);
        Assert.Equal("mixed-target-body", decision.ReasonCode);
    }

    [Fact]
    public void ActualSerializedHeadingSampleIsAcceptedAlongWithBodySample()
    {
        var wire = WireCandidate.Replace(
            "\"samples\":[{\"text\":\"This paragraph is public and has enough English words to translate.\",\"kind\":\"body\"}]",
            "\"samples\":[{\"text\":\"An English heading for the current public article\",\"kind\":\"heading\"}," +
                "{\"text\":\"This paragraph is public and has enough English words to translate.\",\"kind\":\"body\"}]",
            StringComparison.Ordinal);
        using var json = JsonDocument.Parse(wire);
        Assert.Equal("heading", json.RootElement.GetProperty("samples")[0].GetProperty("kind").GetString());
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.True(BrowserPageAutoCoordinator.IsBounded(candidate!));
        Assert.Equal(new[] { "heading", "body" }, candidate!.Samples!.Select(sample => sample.Kind));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinishedAndRejectedBatchesCarryAnEmptyUpdatesArray(bool rejected)
    {
        using var json = JsonDocument.Parse(WireCandidate);
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        using var reply = JsonDocument.Parse(BrowserPageAutoProtocol.Serialize(candidate!,
            new("pageAutoTranslations", Finished: true, Error: rejected ? "automatic-queue-unavailable" : null)));
        Assert.Equal(JsonValueKind.Array, reply.RootElement.GetProperty("updates").ValueKind);
        Assert.Equal(0, reply.RootElement.GetProperty("updates").GetArrayLength());
        Assert.True(reply.RootElement.GetProperty("finished").GetBoolean());
        Assert.Equal(100, reply.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(2, reply.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal("doc-123", reply.RootElement.GetProperty("documentToken").GetString());
        Assert.Equal("batch-1", reply.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(4, reply.RootElement.GetProperty("tabId").GetInt32());
        Assert.Equal(9, reply.RootElement.GetProperty("browserWindowId").GetInt32());
        Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("manualMode").ValueKind);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AssessmentAllowsImageJudgementOnlyWhenPageIsNotMixed(bool mixed, bool expected)
    {
        using var json = JsonDocument.Parse(WireCandidate);
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        using var reply = JsonDocument.Parse(BrowserPageAutoProtocol.Serialize(candidate!, new("pageAutoAssessment",
            new(false, "keep", .99, "zh", mixed) { ReasonCode = mixed ? "mixed-target-body" : "target-body" })));
        Assert.Equal(expected, reply.RootElement.GetProperty("allowImages").GetBoolean());
        Assert.False(reply.RootElement.GetProperty("needsTranslation").GetBoolean());
    }

    [Fact]
    public void OmittedManualModeKeepsTheAutomaticCandidate()
    {
        using var json = JsonDocument.Parse(WireCandidate);
        Assert.False(json.RootElement.TryGetProperty("manualMode", out _));
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate!.ManualMode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("selection")]
    [InlineData("page")]
    public void ExplicitModeAndDocumentIdentityArePreservedInEveryReply(string? mode)
    {
        using var json = JsonDocument.Parse(WithManualMode(JsonSerializer.Serialize(mode)));
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        var accepted = candidate!;
        Assert.Equal(mode, accepted.ManualMode);
        Assert.Equal(Connection, accepted.ConnectionId);
        foreach (var type in new[] { "pageAutoAssessment", "pageAutoTranslations" })
        {
            using var reply = JsonDocument.Parse(BrowserPageAutoProtocol.Serialize(accepted, new(type, Finished: true)));
            Assert.Equal(type, reply.RootElement.GetProperty("type").GetString());
            Assert.Equal(mode, reply.RootElement.GetProperty("manualMode").GetString());
            Assert.Equal("batch-1", reply.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("doc-123", reply.RootElement.GetProperty("documentToken").GetString());
            Assert.Equal(4, reply.RootElement.GetProperty("tabId").GetInt32());
            Assert.Equal(9, reply.RootElement.GetProperty("browserWindowId").GetInt32());
            Assert.Equal(100, reply.RootElement.GetProperty("generation").GetInt64());
            Assert.Equal(2, reply.RootElement.GetProperty("revision").GetInt64());
        }
    }

    [Theory]
    [InlineData("selection", false)]
    [InlineData("selection", true)]
    [InlineData("page", false)]
    [InlineData("page", true)]
    public void ManualAssessmentDoesNotAuthorizeImageJudgement(string mode, bool mixed)
    {
        using var json = JsonDocument.Parse(WithManualMode(JsonSerializer.Serialize(mode)));
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        using var reply = JsonDocument.Parse(BrowserPageAutoProtocol.Serialize(candidate!, new("pageAutoAssessment",
            new(true, "translate", .99, "en", mixed))));
        Assert.True(reply.RootElement.GetProperty("needsTranslation").GetBoolean());
        Assert.False(reply.RootElement.GetProperty("allowImages").GetBoolean());
    }

    [Theory]
    [InlineData("\"auto\"")]
    [InlineData("\"Selection\"")]
    [InlineData("\"PAGE\"")]
    [InlineData("\"selection \"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void InvalidManualModesAreRejectedInsteadOfFallingBackToAutomatic(string modeJson)
    {
        using var json = JsonDocument.Parse(WithManualMode(modeJson));
        Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData("\"requestId\":\"batch-1\"", "\"requestId\":null")]
    [InlineData("\"documentToken\":\"doc-123\"", "\"documentToken\":\"\"")]
    [InlineData("\"tabId\":4", "\"tabId\":-1")]
    [InlineData("\"browserWindowId\":9", "\"browserWindowId\":-1")]
    [InlineData("\"frameId\":0", "\"frameId\":1")]
    [InlineData("\"generation\":100", "\"generation\":9007199254740992")]
    [InlineData("\"revision\":2", "\"revision\":-1")]
    [InlineData("\"hanCharacters\":40", "\"hanCharacters\":400")]
    public void ManualCandidatesStillRequireBoundedDocumentIdentityAndStatistics(string source, string replacement)
    {
        foreach (var mode in new[] { "selection", "page" })
        {
            using var json = JsonDocument.Parse(WithManualMode(JsonSerializer.Serialize(mode))
                .Replace(source, replacement, StringComparison.Ordinal));
            Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
            Assert.Null(candidate);
        }
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("page")]
    public void ManualCandidatesStillRejectTextBeyondTheUploadLimit(string mode)
    {
        const string source = "This paragraph is public and has enough English words to translate.";
        using var json = JsonDocument.Parse(WithManualMode(JsonSerializer.Serialize(mode))
            .Replace(source, new string('x', 32_769), StringComparison.Ordinal));
        Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("page")]
    public void ManualCandidatesCanOmitAutomaticJudgementSamples(string mode)
    {
        const string samples = "\"samples\":[{\"text\":\"This paragraph is public and has enough English words to translate.\",\"kind\":\"body\"}],";
        using var json = JsonDocument.Parse(WithManualMode(JsonSerializer.Serialize(mode))
            .Replace(samples, "", StringComparison.Ordinal));
        Assert.False(json.RootElement.TryGetProperty("samples", out _));
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate!.Samples);
        Assert.Equal(mode, candidate!.ManualMode);
    }

    [Theory]
    [InlineData("selection", "blocks")]
    [InlineData("page", "blocks")]
    [InlineData("selection", "samples")]
    [InlineData("page", "samples")]
    public void ManualCandidatesStillRejectArraysBeyondTheItemLimit(string mode, string field)
    {
        var wire = System.Text.Json.Nodes.JsonNode.Parse(WithManualMode(JsonSerializer.Serialize(mode)))!;
        wire[field] = field == "blocks"
            ? JsonSerializer.SerializeToNode(Enumerable.Range(0, 49).Select(index => new { id = "p-" + index, text = "Paragraph" }))
            : JsonSerializer.SerializeToNode(Enumerable.Range(0, 65).Select(_ => new { text = "Sample", kind = "body" }));
        using var json = JsonDocument.Parse(wire.ToJsonString());
        Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("selection")]
    [InlineData("page")]
    public void SourceGroupMetadataPreservesTheExactUtf8HashAndUtf16Span(string? mode)
    {
        using var json = JsonDocument.Parse(SourceGroupCandidate(mode).ToJsonString());
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        var block = Assert.Single(candidate!.Blocks);
        Assert.Equal("p-1", block.Id);
        Assert.Equal("g-1", block.GroupId);
        Assert.Equal(SourceGroupBlockText, block.Text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceGroupText))), block.SourceGroupHash);
        Assert.Equal(SourceGroupText.Length, block.SourceGroupLength);
        Assert.Equal(SourceGroupText.IndexOf(SourceGroupBlockText, StringComparison.Ordinal), block.SourceStart);
        Assert.Equal(SourceGroupBlockText.Length, block.SourceLength);
        Assert.NotEqual(Encoding.UTF8.GetByteCount(SourceGroupBlockText), block.SourceLength);
        Assert.Equal(mode, candidate!.ManualMode);
        Assert.False(json.RootElement.GetProperty("blocks")[0].TryGetProperty("source", out _));
        Assert.False(json.RootElement.GetProperty("blocks")[0].TryGetProperty("sourceGroupText", out _));
    }

    [Fact]
    public void LegacyBlocksWithoutAnySourceGroupMetadataRemainAccepted()
    {
        using var json = JsonDocument.Parse(WireCandidate);
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        var block = Assert.Single(candidate!.Blocks);
        Assert.Null(block.SourceGroupHash);
        Assert.Null(block.SourceGroupLength);
        Assert.Null(block.SourceStart);
        Assert.Null(block.SourceLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void EveryPartialSourceGroupMetadataShapeIsRejected(int presentFields)
    {
        var wire = SourceGroupCandidate("page");
        var block = wire["blocks"]![0]!.AsObject();
        for (var index = 0; index < SourceGroupFields.Length; index++)
            if ((presentFields & (1 << index)) == 0) block.Remove(SourceGroupFields[index]);
        using var json = JsonDocument.Parse(wire.ToJsonString());
        Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData("sourceGroupHash")]
    [InlineData("sourceGroupLength")]
    [InlineData("sourceStart")]
    [InlineData("sourceLength")]
    [InlineData("all")]
    public void ExplicitNullSourceGroupMetadataIsRejected(string field)
    {
        var wire = SourceGroupCandidate("selection");
        var block = wire["blocks"]![0]!.AsObject();
        var fields = field == "all" ? SourceGroupFields : new[] { field };
        foreach (var name in fields) block[name] = null;
        using var json = JsonDocument.Parse(wire.ToJsonString());
        Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData("sourceGroupHash", "\"\"")]
    [InlineData("sourceGroupHash", "\"0123456789abcdef\"")]
    [InlineData("sourceGroupHash", "\"g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"")]
    [InlineData("sourceGroupHash", "1")]
    [InlineData("sourceGroupHash", "true")]
    [InlineData("sourceGroupHash", "[]")]
    [InlineData("sourceGroupLength", "-1")]
    [InlineData("sourceGroupLength", "0")]
    [InlineData("sourceGroupLength", "1000001")]
    [InlineData("sourceGroupLength", "2147483648")]
    [InlineData("sourceGroupLength", "1.5")]
    [InlineData("sourceGroupLength", "100.0")]
    [InlineData("sourceGroupLength", "\"100\"")]
    [InlineData("sourceGroupLength", "true")]
    [InlineData("sourceGroupLength", "{}")]
    [InlineData("sourceStart", "-1")]
    [InlineData("sourceStart", "2147483647")]
    [InlineData("sourceStart", "2147483648")]
    [InlineData("sourceStart", "1.5")]
    [InlineData("sourceStart", "0.0")]
    [InlineData("sourceStart", "\"0\"")]
    [InlineData("sourceStart", "false")]
    [InlineData("sourceStart", "[]")]
    [InlineData("sourceLength", "-1")]
    [InlineData("sourceLength", "0")]
    [InlineData("sourceLength", "23")]
    [InlineData("sourceLength", "25")]
    [InlineData("sourceLength", "2147483647")]
    [InlineData("sourceLength", "2147483648")]
    [InlineData("sourceLength", "1.5")]
    [InlineData("sourceLength", "24.0")]
    [InlineData("sourceLength", "\"24\"")]
    [InlineData("sourceLength", "true")]
    [InlineData("sourceLength", "{}")]
    public void InvalidSourceGroupMetadataIsRejectedBeforeAnyJudgement(string field, string valueJson)
    {
        var wire = SourceGroupCandidate("page");
        wire["blocks"]![0]![field] = JsonNode.Parse(valueJson);
        using var json = JsonDocument.Parse(wire.ToJsonString());
        Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceGroupSpansAcceptExactBoundariesIncludingTheMaximumGroupLength(bool maximumLength)
    {
        var fullSource = maximumLength
            ? new string('x', 1_000_000 - SourceGroupBlockText.Length) + SourceGroupBlockText
            : SourceGroupBlockText;
        var wire = SourceGroupCandidate("selection");
        var block = wire["blocks"]![0]!;
        block["sourceGroupHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullSource))).ToLowerInvariant();
        block["sourceGroupLength"] = fullSource.Length;
        block["sourceStart"] = fullSource.Length - SourceGroupBlockText.Length;
        using var json = JsonDocument.Parse(wire.ToJsonString());
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        var accepted = Assert.Single(candidate!.Blocks);
        Assert.Equal(fullSource.Length, accepted.SourceGroupLength);
        Assert.Equal(fullSource.Length, accepted.SourceStart!.Value + accepted.SourceLength!.Value);
        Assert.Equal(64, accepted.SourceGroupHash!.Length);
    }

    [Fact]
    public void SourceGroupSpanBeyondTheFullGroupIsRejectedEvenWhenTheBlockLengthMatches()
    {
        var wire = SourceGroupCandidate("page");
        wire["blocks"]![0]!["sourceStart"] = SourceGroupText.Length - SourceGroupBlockText.Length + 1;
        using var json = JsonDocument.Parse(wire.ToJsonString());
        Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void ForeignHanContextDefaultsToFalseAndPreservesOnlyBooleanValues(string? valueJson, bool expected)
    {
        foreach (var hasSourceMetadata in new[] { false, true })
        {
            var wire = hasSourceMetadata ? SourceGroupCandidate("page") : JsonNode.Parse(WireCandidate)!.AsObject();
            if (valueJson is not null) wire["blocks"]![0]!["foreignHanContext"] = JsonNode.Parse(valueJson);
            using var json = JsonDocument.Parse(wire.ToJsonString());
            Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
            Assert.Equal(expected, Assert.Single(candidate!.Blocks).ForeignHanContext);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("\"false\"")]
    [InlineData("\"\"")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void InvalidForeignHanContextTypesAreRejectedWithoutCoercion(string valueJson)
    {
        foreach (var hasSourceMetadata in new[] { false, true })
        {
            var wire = hasSourceMetadata ? SourceGroupCandidate("selection") : JsonNode.Parse(WireCandidate)!.AsObject();
            wire["blocks"]![0]!["foreignHanContext"] = JsonNode.Parse(valueJson);
            using var json = JsonDocument.Parse(wire.ToJsonString());
            Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
            Assert.Null(candidate);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("\"source\"", "source")]
    [InlineData("\"selection\"", "selection")]
    public void ContextKindCanBeOmittedOrPreservedAlongsideForeignHanContext(string? valueJson, string? expected)
    {
        foreach (var hasSourceMetadata in new[] { false, true })
        {
            var wire = hasSourceMetadata ? SourceGroupCandidate("selection") : JsonNode.Parse(WireCandidate)!.AsObject();
            var block = wire["blocks"]![0]!;
            block["foreignHanContext"] = true;
            block["context"] = "A short surrounding context snippet.";
            if (valueJson is not null) block["contextKind"] = JsonNode.Parse(valueJson);
            using var json = JsonDocument.Parse(wire.ToJsonString());
            Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
            var accepted = Assert.Single(candidate!.Blocks);
            Assert.Equal(expected, accepted.ContextKind);
            Assert.True(accepted.ForeignHanContext);
            Assert.Equal("A short surrounding context snippet.", accepted.Context);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"undefined\"")]
    [InlineData("\"page\"")]
    [InlineData("\"SOURCE\"")]
    [InlineData("\"Selection\"")]
    [InlineData("\"selection \"")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ExplicitNullOrInvalidContextKindsAreRejected(string valueJson)
    {
        foreach (var hasSourceMetadata in new[] { false, true })
        {
            var wire = hasSourceMetadata ? SourceGroupCandidate("page") : JsonNode.Parse(WireCandidate)!.AsObject();
            wire["blocks"]![0]!["contextKind"] = JsonNode.Parse(valueJson);
            using var json = JsonDocument.Parse(wire.ToJsonString());
            Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
            Assert.Null(candidate);
        }
    }

    [Fact]
    public void ReplacementWireUsesOriginalNodeIdAndTextCompleteFailedFields()
    {
        using var json = JsonDocument.Parse(WireCandidate);
        Assert.True(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        using var reply = JsonDocument.Parse(BrowserPageAutoProtocol.Serialize(candidate!, new("pageAutoTranslations",
            Updates: [new BrowserPageTranslationUpdate("p-1", "原位译文", true)])));
        var update = Assert.Single(reply.RootElement.GetProperty("updates").EnumerateArray());
        Assert.Equal("p-1", update.GetProperty("id").GetString());
        Assert.Equal("原位译文", update.GetProperty("text").GetString());
        Assert.True(update.GetProperty("complete").GetBoolean());
        Assert.False(update.GetProperty("failed").GetBoolean());
        Assert.False(update.TryGetProperty("source", out _));
        Assert.False(update.TryGetProperty("translation", out _));
        Assert.False(update.TryGetProperty("foreignHanContext", out _));
        Assert.False(update.TryGetProperty("sourceGroupHash", out _));
        Assert.False(update.TryGetProperty("contextKind", out _));
    }

    [Theory]
    [InlineData("\"text\":\"This paragraph is public and has enough English words to translate.\"", "\"text\":null")]
    [InlineData("\"frameId\":0", "\"frameId\":1")]
    [InlineData("\"hanCharacters\":40", "\"hanCharacters\":400")]
    [InlineData("\"revision\":2", "\"revision\":9007199254740992")]
    public void InvalidNativeCandidatesAreRejectedBeforeAnyJudgement(string source, string replacement)
    {
        using var json = JsonDocument.Parse(WireCandidate.Replace(source, replacement, StringComparison.Ordinal));
        Assert.False(BrowserPageAutoProtocol.TryReadCandidate(Connection, json.RootElement, out var candidate));
        Assert.Null(candidate);
    }

    private static string WithManualMode(string modeJson) => WireCandidate.Replace(
        "\"frameId\":0", "\"frameId\":0,\"manualMode\":" + modeJson, StringComparison.Ordinal);

    private static JsonObject SourceGroupCandidate(string? manualMode)
    {
        var wire = JsonNode.Parse(WithManualMode(JsonSerializer.Serialize(manualMode)))!.AsObject();
        var block = wire["blocks"]![0]!;
        block["text"] = SourceGroupBlockText;
        block["sourceGroupHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceGroupText)));
        block["sourceGroupLength"] = SourceGroupText.Length;
        block["sourceStart"] = SourceGroupText.IndexOf(SourceGroupBlockText, StringComparison.Ordinal);
        block["sourceLength"] = SourceGroupBlockText.Length;
        return wire;
    }
}
