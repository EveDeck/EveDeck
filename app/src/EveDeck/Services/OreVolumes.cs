namespace EveDeck.Services;

// Cubic metres per UNIT for every mineable asteroid type in EVE.
//
// GENERATED FROM ESI, NOT WRITTEN BY HAND. Source of truth is the Asteroid category (25) walked via
// /universe/categories/25/ -> /universe/groups/{id}/ -> /universe/types/{id}/, taking published types
// and discarding anything with "Compressed" in the name (compressed ore is manufactured, never mined,
// and carries a different volume that would corrupt the base-mineral fallback below).
//
// Regenerating it by hand is a mistake waiting to happen: an earlier hand-written table asserted that
// every moon ore was 0.1 m3 when the real figure is 10 m3 -- a hundredfold error sitting inside a
// number presented as exact. If ore data changes, re-walk ESI rather than patching entries.
//
// Covers classic asteroid ores, all five moon-ore rarity tiers, Pochven/Triglavian (Bezdnacine,
// Rakovene, Talassonite), the newer nullsec ores (Ducinium, Eifyrium, Griemeer, Hezorime, Kylixium,
// Mordunium, Nocxite, Ueganite, Ytirium) and ice.
internal static class OreVolumes
{
    // Exact in-game type names. Tried first, so a variant with its own listed volume always wins.
    private static readonly Dictionary<string, double> ByExactName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Admixti Mutanite"] = 4,
        ["Amethystic Crystallite"] = 3,
        ["Amperum Mutanite"] = 4,
        ["Arkonor"] = 16,
        ["Arkonor II-Grade"] = 16,
        ["Arkonor III-Grade"] = 16,
        ["Arkonor IV-Grade"] = 16,
        ["Augumene"] = 0.3,
        ["Azure Ice"] = 1000,
        ["Banidine"] = 0.1,
        ["Bezdnacine"] = 16,
        ["Bezdnacine II-Grade"] = 16,
        ["Bezdnacine III-Grade"] = 16,
        ["Bistot"] = 16,
        ["Bistot II-Grade"] = 16,
        ["Bistot III-Grade"] = 16,
        ["Bistot IV-Grade"] = 16,
        ["Bitumens"] = 10,
        ["Blue Ice"] = 1000,
        ["Blue Ice IV-Grade"] = 1000,
        ["Bountiful Loparite"] = 10,
        ["Bountiful Monazite"] = 10,
        ["Bountiful Xenotime"] = 10,
        ["Bountiful Ytterbite"] = 10,
        ["Brimful Bitumens"] = 10,
        ["Brimful Coesite"] = 10,
        ["Brimful Sylvite"] = 10,
        ["Brimful Zeolites"] = 10,
        ["Carnotite"] = 10,
        ["Chromite"] = 10,
        ["Cinnabar"] = 10,
        ["Clear Icicle"] = 1000,
        ["Clear Icicle IV-Grade"] = 1000,
        ["Cobaltite"] = 10,
        ["Coesite"] = 10,
        ["Conflagrati Mutanite"] = 4,
        ["Copious Cobaltite"] = 10,
        ["Copious Euxenite"] = 10,
        ["Copious Scheelite"] = 10,
        ["Copious Titanite"] = 10,
        ["Crokite"] = 16,
        ["Crokite II-Grade"] = 16,
        ["Crokite III-Grade"] = 16,
        ["Crokite IV-Grade"] = 16,
        ["Crystalline Icicle"] = 1000,
        ["Dark Glitter"] = 1000,
        ["Dark Ochre"] = 8,
        ["Dark Ochre II-Grade"] = 8,
        ["Dark Ochre III-Grade"] = 8,
        ["Dark Ochre IV-Grade"] = 8,
        ["Dense Moissanite"] = 0.1,
        ["Ducinium"] = 16,
        ["Ducinium II-Grade"] = 16,
        ["Ducinium III-Grade"] = 16,
        ["Ducinium IV-Grade"] = 16,
        ["Eifyrium"] = 16,
        ["Eifyrium II-Grade"] = 16,
        ["Eifyrium III-Grade"] = 16,
        ["Eifyrium IV-Grade"] = 16,
        ["Euxenite"] = 10,
        ["Gelidus"] = 1000,
        ["Geodite"] = 16,
        ["Glacial Mass"] = 1000,
        ["Glacial Mass IV-Grade"] = 1000,
        ["Glare Crust"] = 1000,
        ["Glistening Bitumens"] = 10,
        ["Glistening Coesite"] = 10,
        ["Glistening Sylvite"] = 10,
        ["Glistening Zeolites"] = 10,
        ["Glowing Carnotite"] = 10,
        ["Glowing Cinnabar"] = 10,
        ["Glowing Pollucite"] = 10,
        ["Glowing Zircon"] = 10,
        ["Gneiss"] = 5,
        ["Gneiss II-Grade"] = 5,
        ["Gneiss III-Grade"] = 5,
        ["Gneiss IV-Grade"] = 5,
        ["Green Arisite"] = 5,
        ["Griemeer"] = 0.8,
        ["Griemeer II-Grade"] = 0.8,
        ["Griemeer III-Grade"] = 0.8,
        ["Griemeer IV-Grade"] = 0.8,
        ["Hedbergite"] = 3,
        ["Hedbergite II-Grade"] = 3,
        ["Hedbergite III-Grade"] = 3,
        ["Hedbergite IV-Grade"] = 3,
        ["Hemorphite"] = 3,
        ["Hemorphite II-Grade"] = 3,
        ["Hemorphite III-Grade"] = 3,
        ["Hemorphite IV-Grade"] = 3,
        ["Hezorime"] = 5,
        ["Hezorime II-Grade"] = 5,
        ["Hezorime III-Grade"] = 5,
        ["Hezorime IV-Grade"] = 5,
        ["Hiemal Tricarboxyl Condensate"] = 6,
        ["Jaspet"] = 2,
        ["Jaspet II-Grade"] = 2,
        ["Jaspet III-Grade"] = 2,
        ["Jaspet IV-Grade"] = 2,
        ["Kangite X-Grade"] = 1,
        ["Kernite"] = 1.2,
        ["Kernite II-Grade"] = 1.2,
        ["Kernite III-Grade"] = 1.2,
        ["Kernite IV-Grade"] = 1.2,
        ["Krystallos"] = 1000,
        ["Kylixium"] = 1.2,
        ["Kylixium II-Grade"] = 1.2,
        ["Kylixium III-Grade"] = 1.2,
        ["Kylixium IV-Grade"] = 1.2,
        ["Lavish Chromite"] = 10,
        ["Lavish Otavite"] = 10,
        ["Lavish Sperrylite"] = 10,
        ["Lavish Vanadinite"] = 10,
        ["Loparite"] = 10,
        ["Lyavite"] = 1.2,
        ["Mercium"] = 0.6,
        ["Mercoxit"] = 40,
        ["Mercoxit II-Grade"] = 40,
        ["Mercoxit III-Grade"] = 40,
        ["Moissanite X-Grade"] = 1,
        ["Monazite"] = 10,
        ["Mordunium"] = 0.1,
        ["Mordunium II-Grade"] = 0.1,
        ["Mordunium III-Grade"] = 0.1,
        ["Mordunium IV-Grade"] = 0.1,
        ["Nephrite"] = 0.1,
        ["Nesosilicate Rakovene"] = 0.5,
        ["Nocxite"] = 4,
        ["Nocxite II-Grade"] = 4,
        ["Nocxite III-Grade"] = 4,
        ["Nocxite IV-Grade"] = 4,
        ["Oeryl"] = 8,
        ["Omber"] = 0.6,
        ["Omber II-Grade"] = 0.6,
        ["Omber III-Grade"] = 0.6,
        ["Omber IV-Grade"] = 0.6,
        ["Otavite"] = 10,
        ["Peregrinus Mutanite"] = 4,
        ["Pithix"] = 2,
        ["Plagioclase"] = 0.35,
        ["Plagioclase II-Grade"] = 0.35,
        ["Plagioclase III-Grade"] = 0.35,
        ["Plagioclase IV-Grade"] = 0.35,
        ["Pollucite"] = 10,
        ["Polycrase X-Grade"] = 1,
        ["Polygypsum"] = 16,
        ["Pyroxeres"] = 0.3,
        ["Pyroxeres 0-Grade"] = 0.3,
        ["Pyroxeres II-Grade"] = 0.3,
        ["Pyroxeres III-Grade"] = 0.3,
        ["Pyroxeres IV-Grade"] = 0.3,
        ["Rakovene"] = 16,
        ["Rakovene II-Grade"] = 16,
        ["Rakovene III-Grade"] = 16,
        ["Raspite X-Grade"] = 1,
        ["Replete Carnotite"] = 10,
        ["Replete Cinnabar"] = 10,
        ["Replete Pollucite"] = 10,
        ["Replete Zircon"] = 10,
        ["Scheelite"] = 10,
        ["Scordite"] = 0.15,
        ["Scordite 0-Grade "] = 0.15,
        ["Scordite II-Grade"] = 0.15,
        ["Scordite III-Grade"] = 0.15,
        ["Scordite IV-Grade"] = 0.15,
        ["Shimmering Chromite"] = 10,
        ["Shimmering Otavite"] = 10,
        ["Shimmering Sperrylite"] = 10,
        ["Shimmering Vanadinite"] = 10,
        ["Shining Loparite"] = 10,
        ["Shining Monazite"] = 10,
        ["Shining Xenotime"] = 10,
        ["Shining Ytterbite"] = 10,
        ["Solis Mutanite"] = 4,
        ["Sperrylite"] = 10,
        ["Spodumain"] = 16,
        ["Spodumain II-Grade"] = 16,
        ["Spodumain III-Grade"] = 16,
        ["Spodumain IV-Grade"] = 16,
        ["Sylvite"] = 10,
        ["Talassonite"] = 16,
        ["Talassonite II-Grade"] = 16,
        ["Talassonite III-Grade"] = 16,
        ["Tenebraet Mutanite"] = 4,
        ["Titanite"] = 10,
        ["Twinkling Cobaltite"] = 10,
        ["Twinkling Euxenite"] = 10,
        ["Twinkling Scheelite"] = 10,
        ["Twinkling Titanite"] = 10,
        ["Tyranite"] = 0.6,
        ["Ueganite"] = 5,
        ["Ueganite II-Grade"] = 5,
        ["Ueganite III-Grade"] = 5,
        ["Ueganite IV-Grade"] = 5,
        ["Vanadinite"] = 10,
        ["Veldspar"] = 0.1,
        ["Veldspar 0-Grade"] = 0.1,
        ["Veldspar II-Grade"] = 0.1,
        ["Veldspar III-Grade"] = 0.1,
        ["Veldspar IV-Grade"] = 0.1,
        ["White Glaze"] = 1000,
        ["White Glaze IV-Grade"] = 1000,
        ["Xenotime"] = 10,
        ["Ytirium"] = 0.6,
        ["Ytirium II-Grade"] = 0.6,
        ["Ytirium III-Grade"] = 0.6,
        ["Ytirium IV-Grade"] = 0.6,
        ["Ytterbite"] = 10,
        ["Zeolites"] = 10,
        ["Zircon"] = 10,
        ["Zuthrine"] = 40,
    };

    // Base mineral, for variants EVE renders but ESI does not list as their own type -- the game
    // writes "Argil Kylixium" and "Kaolin Kylixium" in mining logs, neither of which is an ESI type
    // name, and both are ordinary Kylixium at 1.2 m3.
    //
    // Keys whose variants genuinely disagree on volume are resolved by majority, or omitted entirely
    // when there is no majority, so an ambiguous name degrades to a logged estimate instead of a
    // confident wrong answer.
    private static readonly Dictionary<string, double> ByBaseMineral = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arisite"] = 5,
        ["Arkonor"] = 16,   // majority of 4 variants
        ["Augumene"] = 0.3,
        ["Banidine"] = 0.1,
        ["Bezdnacine"] = 16,   // majority of 3 variants
        ["Bistot"] = 16,   // majority of 4 variants
        ["Bitumens"] = 10,   // majority of 3 variants
        ["Blue"] = 1000,
        ["Carnotite"] = 10,   // majority of 3 variants
        ["Chromite"] = 10,   // majority of 3 variants
        ["Cinnabar"] = 10,   // majority of 3 variants
        ["Clear"] = 1000,
        ["Cobaltite"] = 10,   // majority of 3 variants
        ["Coesite"] = 10,   // majority of 3 variants
        ["Condensate"] = 6,
        ["Crokite"] = 16,   // majority of 4 variants
        ["Crust"] = 1000,
        ["Crystallite"] = 3,
        ["Dark"] = 8,   // majority of 3 variants
        ["Ducinium"] = 16,   // majority of 4 variants
        ["Eifyrium"] = 16,   // majority of 4 variants
        ["Euxenite"] = 10,   // majority of 3 variants
        ["Gelidus"] = 1000,
        ["Geodite"] = 16,
        ["Glacial"] = 1000,
        ["Glaze"] = 1000,
        ["Glitter"] = 1000,
        ["Gneiss"] = 5,   // majority of 4 variants
        ["Griemeer"] = 0.8,   // majority of 4 variants
        ["Hedbergite"] = 3,   // majority of 4 variants
        ["Hemorphite"] = 3,   // majority of 4 variants
        ["Hezorime"] = 5,   // majority of 4 variants
        ["Ice"] = 1000,   // majority of 2 variants
        ["Icicle"] = 1000,   // majority of 2 variants
        ["Jaspet"] = 2,   // majority of 4 variants
        ["Kangite"] = 1,
        ["Kernite"] = 1.2,   // majority of 4 variants
        ["Krystallos"] = 1000,
        ["Kylixium"] = 1.2,   // majority of 4 variants
        ["Loparite"] = 10,   // majority of 3 variants
        ["Lyavite"] = 1.2,
        ["Mass"] = 1000,
        ["Mercium"] = 0.6,
        ["Mercoxit"] = 40,   // majority of 3 variants
        // "Moissanite" omitted: variants disagree with no majority (0.1m3 x1, 1m3 x1)
        ["Monazite"] = 10,   // majority of 3 variants
        ["Mordunium"] = 0.1,   // majority of 4 variants
        ["Mutanite"] = 4,   // majority of 6 variants
        ["Nephrite"] = 0.1,
        ["Nocxite"] = 4,   // majority of 4 variants
        ["Ochre"] = 8,
        ["Oeryl"] = 8,
        ["Omber"] = 0.6,   // majority of 4 variants
        ["Otavite"] = 10,   // majority of 3 variants
        ["Pithix"] = 2,
        ["Plagioclase"] = 0.35,   // majority of 4 variants
        ["Pollucite"] = 10,   // majority of 3 variants
        ["Polycrase"] = 1,
        ["Polygypsum"] = 16,
        ["Pyroxeres"] = 0.3,   // majority of 5 variants
        ["Rakovene"] = 16,   // majority of 4 variants
        ["Raspite"] = 1,
        ["Scheelite"] = 10,   // majority of 3 variants
        ["Scordite"] = 0.15,   // majority of 5 variants
        ["Sperrylite"] = 10,   // majority of 3 variants
        ["Spodumain"] = 16,   // majority of 4 variants
        ["Sylvite"] = 10,   // majority of 3 variants
        ["Talassonite"] = 16,   // majority of 3 variants
        ["Titanite"] = 10,   // majority of 3 variants
        ["Tyranite"] = 0.6,
        ["Ueganite"] = 5,   // majority of 4 variants
        ["Vanadinite"] = 10,   // majority of 3 variants
        ["Veldspar"] = 0.1,   // majority of 5 variants
        ["White"] = 1000,
        ["Xenotime"] = 10,   // majority of 3 variants
        ["Ytirium"] = 0.6,   // majority of 4 variants
        ["Ytterbite"] = 10,   // majority of 3 variants
        ["Zeolites"] = 10,   // majority of 3 variants
        ["Zircon"] = 10,   // majority of 3 variants
        ["Zuthrine"] = 40,
    };

    // "Omber III-Grade" -> Omber (a richness tier, not a different mineral).
    // "Glistening Zeolites" / "Argil Kylixium" -> the mineral is the last word.
    internal static string BaseMineralOf(string oreName)
    {
        var parts = (oreName ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        return parts[^1].EndsWith("-Grade", StringComparison.OrdinalIgnoreCase) ? parts[0] : parts[^1];
    }

    // True when the volume is known rather than estimated. A false result still yields a usable
    // number via the caller's fallback, but the caller is expected to say so rather than pass it off
    // as exact.
    public static bool TryGet(string oreName, out double m3PerUnit)
    {
        m3PerUnit = 0;
        if (string.IsNullOrWhiteSpace(oreName)) return false;
        var name = oreName.Trim();
        if (ByExactName.TryGetValue(name, out m3PerUnit)) return true;
        return ByBaseMineral.TryGetValue(BaseMineralOf(name), out m3PerUnit);
    }
}
