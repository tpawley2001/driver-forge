"""Compare a Driver Forge scan dump with Win32_PnPEntity/SignedDriver (build/check-scan.sh)."""
import json, sys

def load(p):
    return json.loads(open(p, encoding="utf-8-sig").read())

scan, wmi = load(sys.argv[1]), load(sys.argv[2])
if isinstance(wmi, dict): wmi = [wmi]
ours = {d["InstanceId"].upper(): d for d in scan["devices"]}
theirs = {w["id"].upper(): w for w in wmi}
lst = lambda v: [x for x in (v if isinstance(v, list) else [v]) if x is not None]

problems = []
for k in sorted(theirs.keys() - ours.keys()): problems.append(f"missing from scan: {k}")
for k in sorted(ours.keys() - theirs.keys()): problems.append(f"extra in scan: {k}")
fields = {"hardware IDs": 0, "compatible IDs": 0, "problem code": 0, "driver INF": 0, "driver version": 0, "driver date": 0}
for k in sorted(ours.keys() & theirs.keys()):
    o, w = ours[k], theirs[k]
    i = o["installed"] or {}
    checks = [
        ("hardware IDs", o["HardwareIds"], lst(w["hw"])),
        ("compatible IDs", o["CompatibleIds"], lst(w["compat"])),
        ("problem code", o["ProblemCode"], w["problem"]),
    ]
    if w.get("inf"):
        checks += [
            ("driver INF", (i.get("Inf") or "").lower(), w["inf"].lower()),
            ("driver version", i.get("Version") or "", w["ver"] or ""),
            ("driver date", i.get("Date"), w["date"]),
        ]
    for name, a, b in checks:
        if a != b:
            fields[name] += 1
            problems.append(f"{k}: {name}: scan={a!r} wmi={b!r}")

print(f"OS: {scan['os']}")
print(f"devices: scan {len(ours)}, WMI {len(theirs)}, in both {len(ours.keys() & theirs.keys())}")
print(f"with a driver: {sum(1 for d in scan['devices'] if d['installed'])}; needing one: {sum(1 for d in scan['devices'] if d['NeedsDriver'])}")
print("mismatches by field: " + ", ".join(f"{k} {v}" for k, v in fields.items()))
for p in problems[:60]: print("  " + p)
if len(problems) > 60: print(f"  … {len(problems) - 60} more")
print("MATCH" if not problems else f"{len(problems)} differences")
sys.exit(1 if problems else 0)
