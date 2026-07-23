# -*- coding: utf-8 -*-
# Generates Data\catalogue\{family}.json for the Orchestrateur melodic-line mode.
# 4 families x 6 meters x (base + moods). Line cells: spq=4 (simple) / spq=6 (compound, /3 subdivision).
# Chord/nappe artics: spq=24 (felt-beat). Roles reference cell/artic names (meter-independent names).
import json

def beatpats(spq, ternary):
    e = spq // 3 if ternary else spq // 2
    if ternary:
        return {'b': [(0, spq)], 'qe': [(0, 2 * e), (2 * e, e)], 'eq': [(0, e), (e, 2 * e)],
                'eee': [(0, e), (e, e), (2 * e, e)], 'r': []}
    return {'b': [(0, spq)], 'ee': [(0, e), (e, e)], 'r': []}

def make_cell(spq, bp, beats):
    on = []; ln = []
    for bi, name in enumerate(beats):
        for (s, l) in bp[name]:
            on.append(bi * spq + s); ln.append(max(1, l))
    return {"Spq": spq, "On": on, "Len": ln}

def cells_for(spq, bpb, ternary):
    bp = beatpats(spq, ternary)
    mv = 'qe' if ternary else 'ee'
    runbeat = 'eee' if ternary else 'ee'
    full = 'b'
    def tile(fn): return make_cell(spq, bp, [fn(i) for i in range(bpb)])
    C = {}
    C["long"] = {"Spq": spq, "On": [0], "Len": [bpb * spq]}
    C["hold"] = {"Spq": spq, "On": [0], "Len": [min(bpb, 2) * spq]}
    C["halfq"] = tile(lambda i: full)
    if ternary:
        C["flow"] = tile(lambda i: mv)
    else:
        C["flow"] = tile(lambda i: full if i % 2 == 0 else mv)
    if (not ternary) and bpb >= 2:
        on = [0, int(1.5 * spq)]; ln = [int(1.5 * spq), max(1, spq // 2)]
        for i in range(2, bpb): on.append(i * spq); ln.append(spq)
        C["wide"] = {"Spq": spq, "On": on, "Len": ln}
    else:
        C["wide"] = tile(lambda i: full if i != 1 else mv)
    C["up"] = tile(lambda i: runbeat)
    C["bass"] = tile(lambda i: full)
    C["crest"] = {"Spq": spq, "On": [], "Len": []}
    C["echo"] = make_cell(spq, bp, ['r'] * max(0, bpb // 2) + [mv] * (bpb - bpb // 2))
    C["enter"] = make_cell(spq, bp, ['r'] + [full] * (bpb - 1))
    C["conv"] = tile(lambda i: full if i % 2 == 0 else mv)
    return C

def artics_for(bpb, ternary):
    # felt-beat = 24 slices; a COMPOUND (x/8) beat subdivides in 3 (eighth = 8 slices), a SIMPLE beat in 2 (eighth = 12).
    Q = 24; sub = 8 if ternary else 12; nsub = 3 if ternary else 2
    A = {
        "waltz": [(0, 0, Q)] + [(1 + (k % 3), k * sub, sub) for k in range(bpb * nsub)],          # bass + rolling arpeggio
        "alberti": [(0, 0, Q)] + [([1, 3, 2, 3][k % 4], Q + k * sub, sub) for k in range(max(0, (bpb - 1) * nsub))],
        "oom": sum([([(0, i * Q, Q)] if i == 0 else []) + [(1, i * Q, Q), (2, i * Q, Q), (3, i * Q, Q)] for i in range(bpb)], []),
        "slow_arp": [(0, 0, max(Q, (bpb // 2) * Q)), (1, (bpb // 2) * Q, sub), (3, (bpb // 2) * Q + sub, sub), (5, (bpb - 1) * Q, Q)],
        "fast_arp": [(0, 0, Q)] + sum([[(1, i * Q, sub), (3, i * Q + sub, sub)] + ([(5, i * Q + 2 * sub, sub)] if ternary else []) for i in range(1, bpb)], []),
        "block": sum([[(0, i * Q, Q), (1, i * Q, Q), (2, i * Q, Q), (3, i * Q, Q)] for i in range(bpb)], []),
        "cont": [(0, 0, Q)] + [(1 + (k % 3), k * sub, sub) for k in range(bpb * nsub)],           # continuo, subdivisions
        "motor": sum([[(0, k * sub, sub), (1, k * sub, sub), (2, k * sub, sub), (3, k * sub, sub)] for k in range(bpb * nsub)], []),
        "drive": sum([[(0, i * Q, Q), (1, i * Q, sub), (3, i * Q + sub, sub), (5, i * Q, Q)] for i in range(bpb)], []),
        "nappe": [(r, 0, bpb * Q) for r in (1, 2, 3, 5)],
    }
    return {k: {"Spq": 24, "N": [[r, s, l] for (r, s, l) in v]} for k, v in A.items()}

PROFILES = {
    "ghibli": dict(theme="waltz", dev="oom", intro="slow_arp", outro="slow_arp", bassreg=-24, basscell="bass",
                   ct=dict(theme=["crest", "echo", "enter", "conv"], dev=["enter", "hold", "echo", "conv"],
                           intro=["crest", "conv", "crest", "echo"], outro=["conv", "crest", "echo", "crest"])),
    "generic": dict(theme="oom", dev="oom", intro="block", outro="block", bassreg=-24, basscell="bass",
                    ct=dict(theme=["crest", "echo", "enter", "conv"], dev=["enter", "hold", "echo", "conv"],
                            intro=["crest", "conv", "crest", "echo"], outro=["conv", "crest", "echo", "crest"])),
    "bach": dict(theme="cont", dev="block", intro="cont", outro="cont", bassreg=-24, basscell="bass",
                 ct=dict(theme=["crest", "flow", "echo", "flow"], dev=["flow", "echo", "flow", "echo"],
                         intro=["crest", "echo", "crest", "echo"], outro=["echo", "crest", "echo", "crest"])),
    "vivaldi": dict(theme="motor", dev="motor", intro="drive", outro="drive", bassreg=-19, basscell="bass",
                    ct=dict(theme=None, dev=None, intro=None, outro=None)),
}

# cont = contour mode: 0 arc/wave · 1 up · 2 down · 3 static · 4 zigzag · 5 random
MOODS_GHIBLI = {
    u"calme_nostalgique": dict(reg=-5, th="slow_arp", dv="slow_arp", dens="sparse", cont=0),
    u"enjoué_léger": dict(reg=+3, th="waltz", dv="fast_arp", dens="busy", cont=0),
    u"solennel_requiem": dict(reg=-7, th="block", dv="block", dens="sparse", cont=3),
    u"sombre_dramatique": dict(reg=-7, th="oom", dv="oom", dens="mid", cont=2),
    u"valse_dansant": dict(reg=0, th="waltz", dv="oom", dens="mid", cont=0),
    u"épique_majestueux": dict(reg=+2, th="block", dv="oom", dens="busy", cont=1),
}
MOODS_GENERIC = {
    u"Enjoué": dict(reg=+3, th=None, dv=None, dens="busy", cont=0),
    u"Tendre": dict(reg=-2, th=None, dv=None, dens="sparse", cont=0),
    u"Calme": dict(reg=-4, th=None, dv=None, dens="sparse", cont=3),
    u"Méditatif": dict(reg=-3, th=None, dv=None, dens="sparse", cont=3),
    u"Mélancolique": dict(reg=-5, th=None, dv=None, dens="mid", cont=2),
    u"Majestueux": dict(reg=+2, th=None, dv=None, dens="busy", cont=1),
}

SPARSE = {"up": "flow", "wide": "flow"}              # trim the busiest cells; keep flow so the line still sings
BUSY = {"hold": "flow", "wide": "up", "long": "wide"}  # add motion without saturating everything to 'up'

def swap(arr, m): return [m.get(x, x) for x in arr]

# Several LINE templates per role → the generator emits one RoleMotif per template; the seed picks one per generation
# (and a different dev template per dev unit). First template per role = the characteristic/validated one.
BANKS = {
    "lyrical": {  # ghibli / generic
        "theme": [["flow", "wide", "flow", "hold", "flow", "wide", "up", "long"],
                  ["wide", "flow", "up", "hold", "wide", "flow", "up", "long"],
                  ["flow", "flow", "wide", "up", "flow", "hold", "wide", "long"],
                  ["hold", "wide", "flow", "up", "flow", "wide", "up", "long"]],
        "dev": [["flow", "up", "flow", "wide", "flow", "up", "flow", "long"],
                ["up", "flow", "up", "wide", "up", "flow", "up", "long"],
                ["flow", "wide", "up", "flow", "wide", "up", "flow", "long"]],
        "intro": [["hold", "long", "flow", "wide", "hold", "long", "flow", "long"],
                  ["long", "hold", "flow", "wide", "hold", "flow", "wide", "long"]],
        "outro": [["hold", "long", "hold", "long", "halfq", "long", "long", "long"],
                  ["halfq", "long", "hold", "long", "flow", "long", "long", "long"]],
    },
    "flowing": {  # bach
        "theme": [["flow", "flow", "up", "hold", "flow", "flow", "up", "long"],
                  ["flow", "up", "flow", "up", "flow", "up", "flow", "long"],
                  ["up", "flow", "flow", "up", "flow", "flow", "up", "long"]],
        "dev": [["up", "flow", "up", "flow", "up", "flow", "up", "long"],
                ["flow", "up", "up", "flow", "up", "up", "flow", "long"],
                ["up", "up", "flow", "up", "up", "flow", "up", "long"]],
        "intro": [["hold", "flow", "up", "flow", "hold", "flow", "up", "long"],
                  ["flow", "hold", "flow", "up", "flow", "flow", "up", "long"]],
        "outro": [["flow", "hold", "flow", "hold", "flow", "long", "long", "long"],
                  ["hold", "flow", "up", "flow", "hold", "long", "long", "long"]],
    },
    "motor": {  # vivaldi
        "theme": [["up", "up", "flow", "hold", "up", "up", "flow", "long"],
                  ["up", "flow", "up", "up", "up", "flow", "up", "long"],
                  ["up", "up", "up", "flow", "up", "up", "flow", "long"]],
        "dev": [["up", "up", "up", "up", "up", "up", "up", "long"],
                ["up", "up", "flow", "up", "up", "up", "flow", "long"]],
        "intro": [["up", "up", "hold", "up", "up", "up", "hold", "long"],
                  ["up", "hold", "up", "up", "up", "hold", "up", "long"]],
        "outro": [["up", "hold", "up", "hold", "up", "long", "long", "long"],
                  ["up", "up", "hold", "up", "up", "long", "long", "long"]],
    },
}
FAM_BANK = {"ghibli": "lyrical", "generic": "lyrical", "bach": "flowing", "vivaldi": "motor"}

def roles_for(fam):
    P = PROFILES[fam]
    bank = BANKS[FAM_BANK[fam]]
    def ct(r, line):
        c = P["ct"][r]
        if c is None: return list(line)
        return [c[i % len(c)] for i in range(8)]
    out = []
    for r, chord, v in [("intro", P["intro"], 1), ("theme", P["theme"], 2), ("dev", P["dev"], 1), ("outro", P["outro"], 1)]:
        for line in bank[r]:
            out.append({"Role": r, "Line": line, "Counter": ct(r, line), "Chord": chord, "Register": 0, "Voices": v})
    return out

def apply_mood(roles, mood):
    dens = mood["dens"]; swapmap = SPARSE if dens == "sparse" else (BUSY if dens == "busy" else None)
    out = []
    for r in roles:
        nr = dict(r)
        nr["Register"] = r["Register"] + mood["reg"]
        nr["Contour"] = mood.get("cont", 0)
        if swapmap:
            nr["Line"] = swap(r["Line"], swapmap)
            if r["Counter"] is not None: nr["Counter"] = swap(r["Counter"], swapmap)
        if r["Role"] == "theme" and mood.get("th"): nr["Chord"] = mood["th"]
        if r["Role"] == "dev" and mood.get("dv"): nr["Chord"] = mood["dv"]
        out.append(nr)
    return out

METERS = [(2, 4, 2, False), (3, 4, 3, False), (4, 4, 4, False), (6, 8, 2, True), (9, 8, 3, True), (12, 8, 4, True)]

for fam in PROFILES:
    moods = MOODS_GHIBLI if fam == "ghibli" else MOODS_GENERIC
    variants = []
    for (mn, md, bpb, tern) in METERS:
        spq = 6 if tern else 4
        cells = cells_for(spq, bpb, tern)
        artics = artics_for(bpb, tern)
        P = PROFILES[fam]
        base_roles = roles_for(fam)
        def mkvar(mood_name, roles):
            return {"Mood": mood_name, "MeterNum": mn, "MeterDen": md, "BassRegister": P["bassreg"],
                    "BassCell": P["basscell"], "Nappe": "nappe", "Cells": cells, "Artics": artics, "Roles": roles}
        variants.append(mkvar("", base_roles))
        for mname, mood in moods.items():
            variants.append(mkvar(mname, apply_mood(base_roles, mood)))
    json.dump({"Family": fam, "Variants": variants},
              open(r"C:\Users\swe\source\repos\MusicTracker\MusicTracker\Data\catalogue\%s.json" % fam, 'w', encoding='utf-8'),
              ensure_ascii=False, indent=1)
    print("wrote", fam, "variants:", len(variants))
