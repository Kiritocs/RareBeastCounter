namespace RareBeastCounter;

internal static class RareBeastCounterBeastData
{
    public static readonly TrackedBeast[] AllRedBeasts =
    [
        // Craicic (The Deep)
        new("Craicic Chimeral",      ["GemFrogBestiary"],           "cic c"),
        new("Craicic Spider Crab",   ["CrabSpiderBestiary"],        "c sp"),
        new("Craicic Maw",           ["FrogBestiary"],              "cic m"),
        new("Craicic Sand Spitter",  ["SandSpitterBestiary"],       "c san"),
        new("Craicic Savage Crab",   ["CrabParasiteLargeBestiary"], "c sav"),
        new("Craicic Shield Crab",   ["ShieldCrabBestiary"],        "c sh"),
        new("Craicic Squid",         ["SeaWitchSpawnBestiary"],     "sq"),
        new("Craicic Vassal",        ["ParasiticSquidBestiary"],    "c v"),
        new("Craicic Watcher",       ["SquidBestiary"],             "c wa"),

        // Farric (The Wilds) — Chieftain must precede Ape so "BestiaryMonkey" substring matches correctly
        new("Farric Tiger Alpha",         ["TigerBestiary"],             "c ti"),
        new("Farric Wolf Alpha",          ["WolfBestiary"],              "f a"),
        new("Farric Lynx Alpha",          ["LynxBestiary"],              "c l"),
        new("Farric Flame Hellion Alpha", ["HellionBestiary"],           "c fl"),
        new("Farric Magma Hound",         ["HoundBestiary"],             "ma h"),
        new("Farric Pit Hound",           ["PitbullBestiary"],           "c pi"),
        new("Farric Chieftain",           ["BestiaryMonkeyChiefBlood"], "rric c"),
        new("Farric Ape",                 ["BestiaryMonkey", "MonkeyBloodBestiary"], "c a"),
        new("Farric Goliath",             ["BestiarySpiker"],            "c gol"),
        new("Farric Goatman",             ["GoatmanLeapSlamBestiary"],  "c goa"),
        new("Farric Gargantuan",          ["BeastCaveBestiary"],        "c ga"),
        new("Farric Taurus",              ["BestiaryBull"],             "ic ta"),
        new("Farric Ursa",                ["DropBearBestiary"],         "c u"),
        new("Vicious Hound",              ["PurgeHoundBestiary"],       "s ho"),

        // Fenumal (The Caverns)
        new("Fenumal Hybrid Arachnid",  ["SpiderPlatedBestiary"],   "l hy"),
        new("Fenumal Plagued Arachnid", ["SpiderPlagueBestiary"],   "l pla"),
        new("Fenumal Devourer",         ["RootSpiderBestiary"],     "mal d"),
        new("Fenumal Queen",            ["InsectSpawnerBestiary"],  "l q"),
        new("Fenumal Widow",            ["Spider5Bestiary"],        "l w"),
        new("Fenumal Scorpion",         ["BlackScorpionBestiary"],  "l sco"),
        new("Fenumal Scrabbler",        ["SandLeaperBestiary"],     "l scr"),

        // Saqawine (The Sands)
        new("Saqawine Rhex",        ["MarakethBirdBestiary"], "e rhe"),
        new("Saqawine Vulture",     ["VultureBestiary"],      "e vu"),
        new("Saqawine Cobra",       ["SnakeBestiary"],        "ne co"),
        new("Saqawine Blood Viper", ["SnakeBestiary2"],       "ne b"),
        new("Saqawine Retch",       ["KiwethBestiary"],       "ne re"),
        new("Saqawine Rhoa",        ["RhoaBestiary"],         "ine rho"),
        new("Saqawine Chimeral",    ["IguanaBestiary"],       "ne ch"),

        // Spirit Bosses
        new("Saqawal, First of the Sky",    ["MarakethBirdSpiritBoss"],         "al, f"),
        new("Craiceann, First of the Deep", ["NessaCrabBestiarySpiritBoss"],    "n, f"),
        new("Farrul, First of the Plains",  ["TigerBestiarySpiritBoss"],        "ul, f"),
        new("Fenumus, First of the Night",  ["SpiderPlatedBestiarySpiritBoss"], "s, f"),

        // Harvest T3 & special
        new("Wild Bristle Matron",   ["HarvestBeastT3"],            "le m"),
        new("Wild Hellion Alpha",    ["HarvestHellionT3"],          "ld h"),
        new("Wild Brambleback",      ["HarvestBrambleHulkT3"],      "d bra"),
        new("Primal Cystcaller",     ["HarvestGoatmanT3"],          "cy"),
        new("Primal Rhex Matriarch", ["HarvestRhexT3"],             "x ma"),
        new("Primal Crushclaw",      ["HarvestNessaCrabT3"],        "l cru"),
        new("Vivid Vulture",         ["HarvestVultureParasiteT3"],  "id v"),
        new("Vivid Watcher",         ["HarvestSquidT3"],            "id w"),
        new("Vivid Abberarach",      ["HarvestPlatedScorpionT3"],   "d ab"),
        new("Black Mórrigan",        ["GullGoliathBestiary", "Morrigan"], "k m"),
    ];

    public static readonly string[] DefaultEnabledBeasts =
    [
        "Farrul, First of the Plains",
        "Fenumus, First of the Night",
        "Vivid Vulture",
        "Wild Bristle Matron",
        "Wild Hellion Alpha",
        "Wild Brambleback",
        "Craicic Chimeral",
        "Fenumal Plagued Arachnid",
        "Vicious Hound",
        "Black Mórrigan",
    ];
}

public readonly record struct TrackedBeast(string Name, string[] MetadataPatterns, string RegexFragment);
