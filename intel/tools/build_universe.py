import csv
import json
import os

SDE_DIR = os.path.join(os.path.dirname(__file__), "sde")
OUT_PATH = os.path.join(
    os.path.dirname(__file__), "..", "shared", "src", "commonMain", "resources", "universe.json"
)


def read_csv(name):
    path = os.path.join(SDE_DIR, name)
    with open(path, encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))


def main():
    skipped_system_ids = set()

    systems = []
    for row in read_csv("mapSolarSystems.csv"):
        region_id = int(row["regionID"])
        system_id = int(row["solarSystemID"])
        if region_id >= 11000000:
            skipped_system_ids.add(system_id)
            continue
        systems.append(
            {
                "i": system_id,
                "n": row["solarSystemName"],
                "r": region_id,
                "c": int(row["constellationID"]),
                "s": round(float(row["security"]), 3),
                "x": float(row["x"]),
                "y": float(row["y"]),
                "z": float(row["z"]),
            }
        )
    systems.sort(key=lambda s: s["i"])

    regions = [
        {"i": int(row["regionID"]), "n": row["regionName"]}
        for row in read_csv("mapRegions.csv")
    ]
    regions.sort(key=lambda r: r["i"])

    constellations = [
        {
            "i": int(row["constellationID"]),
            "n": row["constellationName"],
            "r": int(row["regionID"]),
        }
        for row in read_csv("mapConstellations.csv")
    ]
    constellations.sort(key=lambda c: c["i"])

    jump_pairs = set()
    for row in read_csv("mapSolarSystemJumps.csv"):
        from_id = int(row["fromSolarSystemID"])
        to_id = int(row["toSolarSystemID"])
        if from_id in skipped_system_ids or to_id in skipped_system_ids:
            continue
        jump_pairs.add((min(from_id, to_id), max(from_id, to_id)))
    jumps = sorted(jump_pairs)
    jumps = [list(pair) for pair in jumps]

    group_to_category = {
        int(row["groupID"]): int(row["categoryID"]) for row in read_csv("invGroups.csv")
    }

    ships = []
    for row in read_csv("invTypes.csv"):
        if row["published"] != "1":
            continue
        group_id = int(row["groupID"])
        if group_to_category.get(group_id) != 6:
            continue
        ships.append({"i": int(row["typeID"]), "n": row["typeName"]})
    ships.sort(key=lambda s: s["i"])

    obj = {
        "systems": systems,
        "regions": regions,
        "constellations": constellations,
        "jumps": jumps,
        "ships": ships,
    }

    os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
    with open(OUT_PATH, "w", encoding="utf-8") as f:
        json.dump(obj, f, separators=(",", ":"), ensure_ascii=False)


if __name__ == "__main__":
    main()
