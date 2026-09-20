using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveDeck.Services.Intel;

// Copied from EveDeck-Intel shared/src/commonMain/resources/universe.json
// at commit 720eac688f1ec99796fb76fd8edaa7eda73ea923, sha256 eea0d2761303854132de90bc92a7254b00d8a04a95bf1ea0e01c583555531f77

public sealed record SystemDto(
    [property: JsonPropertyName("i")] int I,
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("r")] int R,
    [property: JsonPropertyName("c")] int C,
    [property: JsonPropertyName("s")] double S,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z);

public sealed record RegionDto(
    [property: JsonPropertyName("i")] int I,
    [property: JsonPropertyName("n")] string N);

public sealed record ConstellationDto(
    [property: JsonPropertyName("i")] int I,
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("r")] int R);

public sealed record ShipDto(
    [property: JsonPropertyName("i")] int I,
    [property: JsonPropertyName("n")] string N);

public sealed record UniverseDto(
    [property: JsonPropertyName("systems")] List<SystemDto> Systems,
    [property: JsonPropertyName("regions")] List<RegionDto> Regions,
    [property: JsonPropertyName("constellations")] List<ConstellationDto> Constellations,
    [property: JsonPropertyName("jumps")] List<List<int>> Jumps,
    [property: JsonPropertyName("ships")] List<ShipDto> Ships);

public sealed record SolarSystem(
    int Id,
    string Name,
    int RegionId,
    string RegionName,
    int ConstellationId,
    double Security,
    double X,
    double Y,
    double Z)
{
    public bool IsNullsec => Security < 0.05;
}

public sealed record Region(int Id, string Name);

public sealed record Constellation(int Id, string Name, int RegionId);

public sealed record ShipType(int Id, string Name);

public sealed class Universe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    public IReadOnlyList<SolarSystem> Systems { get; }
    public IReadOnlyDictionary<int, SolarSystem> SystemsById { get; }
    private readonly Dictionary<string, SolarSystem> _systemsByLowerName;

    public IReadOnlyDictionary<int, string> Regions { get; }
    public IReadOnlyDictionary<string, int> RegionIdsByLowerName { get; }

    public IReadOnlyList<ShipType> Ships { get; }
    private readonly Dictionary<string, int> _shipTypeIdsByLowerName;

    private readonly Dictionary<int, int[]> _adjacency;

    public Universe(UniverseDto dto)
    {
        var regionNames = dto.Regions.ToDictionary(r => r.I, r => r.N);

        Systems = dto.Systems
            .Select(s => new SolarSystem(
                Id: s.I,
                Name: s.N,
                RegionId: s.R,
                RegionName: regionNames.GetValueOrDefault(s.R, "Unknown"),
                ConstellationId: s.C,
                Security: s.S,
                X: s.X,
                Y: s.Y,
                Z: s.Z))
            .ToList();

        SystemsById = Systems.ToDictionary(s => s.Id);
        _systemsByLowerName = Systems.ToDictionary(s => s.Name.ToLowerInvariant());

        Regions = regionNames;
        RegionIdsByLowerName = dto.Regions.ToDictionary(r => r.N.ToLowerInvariant(), r => r.I);

        Ships = dto.Ships.Select(s => new ShipType(s.I, s.N)).ToList();
        _shipTypeIdsByLowerName = dto.Ships.ToDictionary(s => s.N.ToLowerInvariant(), s => s.I);

        var adjacency = new Dictionary<int, List<int>>();
        foreach (var pair in dto.Jumps)
        {
            var a = pair[0];
            var b = pair[1];
            if (!adjacency.TryGetValue(a, out var aList)) adjacency[a] = aList = new List<int>();
            aList.Add(b);
            if (!adjacency.TryGetValue(b, out var bList)) adjacency[b] = bList = new List<int>();
            bList.Add(a);
        }
        _adjacency = adjacency.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    public static Universe Parse(string json) =>
        new(JsonSerializer.Deserialize<UniverseDto>(json, JsonOptions)
            ?? throw new InvalidDataException("universe.json deserialized to null"));

    public static Universe Parse(Stream stream) =>
        new(JsonSerializer.Deserialize<UniverseDto>(stream, JsonOptions)
            ?? throw new InvalidDataException("universe.json deserialized to null"));

    public static Universe LoadFromEmbeddedResource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "EveDeck.Resources.universe.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
        return Parse(stream);
    }

    public SolarSystem? System(int id) => SystemsById.GetValueOrDefault(id);

    public SolarSystem? SystemByName(string name) =>
        _systemsByLowerName.GetValueOrDefault(name.ToLowerInvariant());

    public int? ShipTypeIdByName(string name) =>
        _shipTypeIdsByLowerName.TryGetValue(name.ToLowerInvariant(), out var id) ? id : null;

    public int[] Neighbours(int systemId) => _adjacency.GetValueOrDefault(systemId, Array.Empty<int>());

    public IEnumerable<SolarSystem> SystemsInRegions(IEnumerable<int> regionIds)
    {
        var set = regionIds.ToHashSet();
        return Systems.Where(s => set.Contains(s.RegionId));
    }

    public IReadOnlyDictionary<int, int> DistancesFrom(int origin) => DistancesFrom([origin]);

    public IReadOnlyDictionary<int, int> DistancesFrom(IEnumerable<int> origins)
    {
        var seeds = origins.Where(o => SystemsById.ContainsKey(o)).Distinct().ToArray();
        if (seeds.Length == 0) return new Dictionary<int, int>();

        var distances = new Dictionary<int, int>(SystemsById.Count);
        foreach (var seed in seeds) distances[seed] = 0;

        var frontier = seeds;
        var depth = 0;
        while (frontier.Length > 0)
        {
            depth++;
            var next = new List<int>();
            foreach (var node in frontier)
            {
                foreach (var neighbour in Neighbours(node))
                {
                    if (!distances.ContainsKey(neighbour))
                    {
                        distances[neighbour] = depth;
                        next.Add(neighbour);
                    }
                }
            }
            frontier = next.ToArray();
        }
        return distances;
    }

    public int? Jumps(int from, int to)
    {
        if (from == to) return 0;
        return DistancesFrom(new[] { from }).TryGetValue(to, out var d) ? d : null;
    }
}
