using EveDeck.Models.Intel;

namespace EveDeck.Services.Intel.Wire;

/// <summary>Projects the app's own intel types onto the wire DTOs the tablet and web UI expect.</summary>
public static class WireMapper
{
    public static WireIntelMessage ToWire(this IntelMessage message) => new(
        message.Id,
        message.Channel,
        message.Author,
        message.TimestampMillis,
        message.Raw,
        message.Tokens.Select(ToWire).ToList());

    public static WireCharacterLocation ToWire(this CharacterLocation location) => new(
        location.CharacterName,
        location.SystemId,
        location.SystemName,
        location.SinceMillis);

    private static WireToken ToWire(IntelToken token) => token switch
    {
        IntelToken.SystemToken t => new WireToken.SystemToken
        {
            Text = t.Text,
            SystemId = t.SystemId,
            Name = t.Name,
            Linked = t.Linked,
            Inferred = t.Inferred,
        },
        IntelToken.PlayerToken t => new WireToken.PlayerToken { Text = t.Text, Linked = t.Linked },
        IntelToken.ShipToken t => new WireToken.ShipToken
        {
            Text = t.Text,
            TypeId = t.TypeId,
            Name = t.Name,
            Count = t.Count,
            Linked = t.Linked,
        },
        IntelToken.CountToken t => new WireToken.CountToken
        {
            Text = t.Text,
            Value = t.Value,
            Modifier = WireProtocol.ToWire(t.Modifier),
        },
        IntelToken.KeywordToken t => new WireToken.KeywordToken
        {
            Text = t.Text,
            Keyword = WireProtocol.ToWire(t.Keyword),
        },
        IntelToken.QuestionToken t => new WireToken.QuestionToken
        {
            Text = t.Text,
            Kind = WireProtocol.ToWire(t.Kind),
        },
        IntelToken.UrlToken t => new WireToken.UrlToken { Text = t.Text },
        IntelToken.WordToken t => new WireToken.WordToken { Text = t.Text },
        _ => throw new ArgumentOutOfRangeException(nameof(token), token.GetType().Name, "unmapped token type"),
    };
}
