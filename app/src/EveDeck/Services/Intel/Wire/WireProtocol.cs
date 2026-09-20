using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveDeck.Services.Intel.Wire;

// The on-the-wire JSON contract of EveDeck Intel's WebSocket, ported field-for-field from
// EveDeck-Intel shared/wire/Protocol.kt and shared/model/Intel.kt so the existing Android app and
// web UI can talk to EveDeck without knowing which implementation is serving them.
//
// Every name here is load-bearing. Kotlin serialises with classDiscriminator = "type",
// encodeDefaults = true and SCREAMING_SNAKE enum names; a single renamed field or enum value
// produces a payload the client silently cannot read rather than an error. WireProtocolTests pins
// the whole shape -- change nothing here without changing those.

/// <summary>How the tablet's feed renders, synced from the PC rather than kept per-device.</summary>
public sealed record WireDisplaySettings
{
    public const string DefaultTextColor = "#D7E1EC";
    private const float MinScale = 0.75f;
    private const float MaxScale = 1.75f;

    public float FontScale { get; init; } = 1f;
    public float IconScale { get; init; } = 1f;
    public string TextColor { get; init; } = DefaultTextColor;
    public bool GlowEnabled { get; init; }
    public bool DropShadowEnabled { get; init; }

    /// <summary>Keeps a corrupt config or a bad client value from rendering an unreadable feed.</summary>
    public WireDisplaySettings Coerced() => this with
    {
        FontScale = Math.Clamp(FontScale, MinScale, MaxScale),
        IconScale = Math.Clamp(IconScale, MinScale, MaxScale),
        TextColor = IsHexColor(TextColor) ? TextColor : DefaultTextColor,
    };

    private static bool IsHexColor(string value) =>
        value.Length == 7
        && value[0] == '#'
        && value.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>A chat channel the server has log files for.</summary>
public sealed record WireChannelInfo(
    string Name,
    int FileCount,
    long LastActivityMillis,
    bool Reserved = false);

/// <summary>
/// Nested inside a snapshot as a concrete type, which is why this exists separately from
/// <see cref="WireServerMessage.ChannelsMessage"/>: Kotlin only writes the "type" discriminator when
/// the declared type is the sealed interface, so the copy nested in a snapshot carries no "type" key.
/// </summary>
public sealed record WireChannels(
    IReadOnlyList<WireChannelInfo> Available,
    IReadOnlyList<string> Selected);

public sealed record WireCharacterInfo(
    string Name,
    long CharacterId,
    long? CorporationId = null,
    string? CorporationName = null,
    string? CorporationTicker = null,
    long? AllianceId = null,
    string? AllianceName = null,
    string? AllianceTicker = null);

public sealed record WireCharacterLocation(
    string CharacterName,
    int SystemId,
    string SystemName,
    long SinceMillis);

public sealed record WireIntelMessage(
    string Id,
    string Channel,
    string Author,
    long TimestampMillis,
    string Raw,
    IReadOnlyList<WireToken> Tokens);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SystemToken), "system")]
[JsonDerivedType(typeof(PlayerToken), "player")]
[JsonDerivedType(typeof(ShipToken), "ship")]
[JsonDerivedType(typeof(CountToken), "count")]
[JsonDerivedType(typeof(KeywordToken), "keyword")]
[JsonDerivedType(typeof(QuestionToken), "question")]
[JsonDerivedType(typeof(UrlToken), "url")]
[JsonDerivedType(typeof(WordToken), "word")]
public abstract record WireToken
{
    public required string Text { get; init; }

    public sealed record SystemToken : WireToken
    {
        public required int SystemId { get; init; }
        public required string Name { get; init; }
        public bool Linked { get; init; }
        public bool Inferred { get; init; }
    }

    public sealed record PlayerToken : WireToken
    {
        public bool Linked { get; init; }
    }

    public sealed record ShipToken : WireToken
    {
        public int? TypeId { get; init; }
        public required string Name { get; init; }
        public int? Count { get; init; }
        public bool Linked { get; init; }
    }

    public sealed record CountToken : WireToken
    {
        public required int Value { get; init; }
        public required string Modifier { get; init; }
    }

    public sealed record KeywordToken : WireToken
    {
        public required string Keyword { get; init; }
    }

    public sealed record QuestionToken : WireToken
    {
        public required string Kind { get; init; }
    }

    public sealed record UrlToken : WireToken;

    public sealed record WordToken : WireToken;
}

public sealed record WireSovHolder(long Id, string Name, string? Ticker = null, string Kind = "alliance");

public sealed record WireSystemStats(
    int SystemId,
    int ShipKills = 0,
    int PodKills = 0,
    int NpcKills = 0,
    int Jumps = 0);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Snapshot), "snapshot")]
[JsonDerivedType(typeof(ChannelsMessage), "channels")]
[JsonDerivedType(typeof(Intel), "intel")]
[JsonDerivedType(typeof(Location), "location")]
[JsonDerivedType(typeof(Characters), "characters")]
[JsonDerivedType(typeof(Sovereignty), "sovereignty")]
[JsonDerivedType(typeof(Stats), "stats")]
[JsonDerivedType(typeof(Heartbeat), "heartbeat")]
[JsonDerivedType(typeof(Display), "display")]
public abstract record WireServerMessage
{
    /// <summary>Sent once on connect, so a client joining mid-fight sees recent history immediately.</summary>
    public sealed record Snapshot : WireServerMessage
    {
        public required IReadOnlyList<WireIntelMessage> Messages { get; init; }
        public required IReadOnlyList<WireCharacterLocation> Locations { get; init; }
        public required IReadOnlyList<int> ScopeRegionIds { get; init; }
        public required long ServerTimeMillis { get; init; }
        public WireChannels Channels { get; init; } = new([], []);

        // Renamed in C# only, to avoid shadowing the same-named nested message types. The wire names
        // are what matter.
        [JsonPropertyName("display")]
        public WireDisplaySettings DisplayValue { get; init; } = new();

        [JsonPropertyName("characters")]
        public IReadOnlyList<WireCharacterInfo> CharactersValue { get; init; } = [];
    }

    public sealed record ChannelsMessage : WireServerMessage
    {
        public IReadOnlyList<WireChannelInfo> Available { get; init; } = [];
        public IReadOnlyList<string> Selected { get; init; } = [];
    }

    public sealed record Intel : WireServerMessage
    {
        public required WireIntelMessage Message { get; init; }
    }

    public sealed record Location : WireServerMessage
    {
        // Renamed in C# only to avoid clashing with the enclosing type name; the wire name must stay
        // "location".
        [JsonPropertyName("location")]
        public required WireCharacterLocation LocationValue { get; init; }
    }

    public sealed record Characters : WireServerMessage
    {
        [JsonPropertyName("characters")]
        public IReadOnlyList<WireCharacterInfo> CharactersValue { get; init; } = [];
    }

    public sealed record Sovereignty : WireServerMessage
    {
        public IReadOnlyList<WireSovHolder> Holders { get; init; } = [];

        /// <summary>systemId -> holder id. Systems with no sov are simply absent.</summary>
        public IReadOnlyDictionary<int, long> Systems { get; init; } = new Dictionary<int, long>();
    }

    public sealed record Stats : WireServerMessage
    {
        public IReadOnlyList<WireSystemStats> Systems { get; init; } = [];
    }

    public sealed record Heartbeat : WireServerMessage
    {
        public required long ServerTimeMillis { get; init; }
    }

    public sealed record Display : WireServerMessage
    {
        public WireDisplaySettings Settings { get; init; } = new();
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Follow), "follow")]
[JsonDerivedType(typeof(SetChannels), "setChannels")]
[JsonDerivedType(typeof(SetDisplay), "setDisplay")]
public abstract record WireClientMessage
{
    public sealed record Hello : WireClientMessage
    {
        public string DeviceName { get; init; } = "";
        public int ProtocolVersion { get; init; } = WireProtocol.ProtocolVersion;
    }

    public sealed record Follow : WireClientMessage
    {
        public string? CharacterName { get; init; }
    }

    public sealed record SetChannels : WireClientMessage
    {
        public IReadOnlyList<string> Channels { get; init; } = [];
    }

    public sealed record SetDisplay : WireClientMessage
    {
        public WireDisplaySettings Settings { get; init; } = new();
    }
}

public static class WireProtocol
{
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Mirrors Kotlin's WireJson: camelCase fields, defaults always written, unknown keys tolerated.
    /// Defaults must be emitted — the client relies on them being present rather than inferred.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // Kotlin serialises enums by their declaration name, which is SCREAMING_SNAKE_CASE. These maps
    // are explicit rather than attribute-driven so a renamed C# member cannot silently change the
    // wire value; WireProtocolTests asserts every enum member is covered.
    public static string ToWire(Models.Intel.IntelKeyword keyword) => keyword switch
    {
        Models.Intel.IntelKeyword.Clear => "CLEAR",
        Models.Intel.IntelKeyword.Status => "STATUS",
        Models.Intel.IntelKeyword.NoVisual => "NO_VISUAL",
        Models.Intel.IntelKeyword.Spike => "SPIKE",
        Models.Intel.IntelKeyword.GateCamp => "GATE_CAMP",
        Models.Intel.IntelKeyword.Bubbles => "BUBBLES",
        Models.Intel.IntelKeyword.Wormhole => "WORMHOLE",
        Models.Intel.IntelKeyword.Cyno => "CYNO",
        Models.Intel.IntelKeyword.Ess => "ESS",
        Models.Intel.IntelKeyword.Skyhook => "SKYHOOK",
        Models.Intel.IntelKeyword.CombatProbes => "COMBAT_PROBES",
        Models.Intel.IntelKeyword.Docked => "DOCKED",
        Models.Intel.IntelKeyword.DScan => "D_SCAN",
        Models.Intel.IntelKeyword.Neutral => "NEUTRAL",
        Models.Intel.IntelKeyword.Hostile => "HOSTILE",
        _ => throw new ArgumentOutOfRangeException(nameof(keyword), keyword, "unmapped intel keyword"),
    };

    public static string ToWire(Models.Intel.CountModifier modifier) => modifier switch
    {
        Models.Intel.CountModifier.Exact => "EXACT",
        Models.Intel.CountModifier.Plus => "PLUS",
        Models.Intel.CountModifier.Total => "TOTAL",
        Models.Intel.CountModifier.Times => "TIMES",
        _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, "unmapped count modifier"),
    };

    public static string ToWire(Models.Intel.QuestionKind kind) => kind switch
    {
        Models.Intel.QuestionKind.Location => "LOCATION",
        Models.Intel.QuestionKind.ShipType => "SHIP_TYPE",
        Models.Intel.QuestionKind.Status => "STATUS",
        Models.Intel.QuestionKind.Count => "COUNT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unmapped question kind"),
    };

    public static string Serialize(WireServerMessage message) =>
        JsonSerializer.Serialize(message, Json);

    public static WireClientMessage? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WireClientMessage>(json, Json);
        }
        catch (JsonException)
        {
            return null; // a malformed frame must never take the server down
        }
    }
}
