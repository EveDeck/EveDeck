using System.Text.Json.Serialization;

namespace EveDeck.Models;

// ESI response shapes for the per-seat preview info flyout and the skill-queue alerts (added
// 2026-07-24). All read-only public/character endpoints; see EsiAuthService for the scopes they need.
// Deserialized by CharacterInfoService via EsiClient.GetAsync. Type ids (skill/ship) are resolved to
// display names by the existing EsiTypeCache; system ids by EsiTypeCache.GetSystemNameAsync.

// One entry of GET /characters/{id}/skillqueue/ (esi-skills.read_skillqueue.v1). The queue is returned
// as a JSON array ordered by QueuePosition. FinishDate/StartDate are absent (null) when the queue is
// paused or the character is an Alpha whose training has lapsed, so treat null as "not training".
public sealed class EsiSkillQueueEntry
{
    [JsonPropertyName("skill_id")] public int SkillId { get; set; }
    [JsonPropertyName("finished_level")] public int FinishedLevel { get; set; }
    [JsonPropertyName("queue_position")] public int QueuePosition { get; set; }
    [JsonPropertyName("start_date")] public DateTimeOffset? StartDate { get; set; }
    [JsonPropertyName("finish_date")] public DateTimeOffset? FinishDate { get; set; }
    [JsonPropertyName("level_start_sp")] public long? LevelStartSp { get; set; }
    [JsonPropertyName("level_end_sp")] public long? LevelEndSp { get; set; }
    [JsonPropertyName("training_start_sp")] public long? TrainingStartSp { get; set; }
}

// GET /characters/{id}/location/ (esi-location.read_location.v1). StationId/StructureId are set only
// when docked; SolarSystemId is always present.
public sealed class EsiCharacterLocation
{
    [JsonPropertyName("solar_system_id")] public int SolarSystemId { get; set; }
    [JsonPropertyName("station_id")] public long? StationId { get; set; }
    [JsonPropertyName("structure_id")] public long? StructureId { get; set; }
}

// GET /characters/{id}/ship/ (esi-location.read_ship_type.v1). ShipName is the player-assigned fitting
// name; ShipTypeId resolves to the hull name (e.g. "Rifter") via EsiTypeCache.
public sealed class EsiCharacterShip
{
    [JsonPropertyName("ship_type_id")] public int ShipTypeId { get; set; }
    [JsonPropertyName("ship_item_id")] public long ShipItemId { get; set; }
    [JsonPropertyName("ship_name")] public string ShipName { get; set; } = "";
}

// GET /universe/structures/{structure_id}/ (esi-universe.read_structures.v1). Access-gated: returns a
// name only for structures the authenticated character can dock at. Used for the flyout's docked line.
public sealed class EsiStructureInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("solar_system_id")] public int SolarSystemId { get; set; }
    [JsonPropertyName("type_id")] public int TypeId { get; set; }
}

// GET /characters/{id}/fatigue/ (esi-characters.read_fatigue.v1). All fields absent (null) for a
// character that has never used a jump drive/bridge, which reads as "no fatigue". JumpFatigueExpireDate
// is the blue timer; the character can't jump again without penalty until it passes.
public sealed class EsiCharacterFatigue
{
    [JsonPropertyName("last_jump_date")] public DateTimeOffset? LastJumpDate { get; set; }
    [JsonPropertyName("jump_fatigue_expire_date")] public DateTimeOffset? JumpFatigueExpireDate { get; set; }
    [JsonPropertyName("last_update_date")] public DateTimeOffset? LastUpdateDate { get; set; }
}
