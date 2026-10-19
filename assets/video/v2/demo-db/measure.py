"""Drives the SchemaScope API (dev server on 127.0.0.1:5199) against the LocalDB demo databases.

    python measure.py connections      # add/reuse the 10 SSDemo_* connections
    python measure.py compare [N]      # SSDemo_Dev vs all 9 targets N times (default 5) + 1 target alone
    python measure.py hero             # write mode on, default apply to SSDemo_Client05, re-compare, write mode off

Results are written to measurements.json next to this file.
"""
import json
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

API = "http://127.0.0.1:5199"
TOKEN = "schemascope-dev"
SERVER = r"(localdb)\MSSQLLocalDB"
HERE = Path(__file__).parent
RESULTS = HERE / "measurements.json"
TARGETS = ["SSDemo_QA"] + [f"SSDemo_Client{i:02d}" for i in range(1, 9)]
HERO = "SSDemo_Client05"


def call(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method,
                                 headers={"X-SchemaScope-Token": TOKEN, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=3600) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"{method} {path} -> {e.code}: {e.read().decode(errors='replace')}")


def load():
    return json.loads(RESULTS.read_text()) if RESULTS.exists() else {}


def save(d):
    RESULTS.write_text(json.dumps(d, indent=2))


def connections():
    existing = {c["label"]: c for c in call("GET", "/api/connections")}
    ids = {}
    for db in ["SSDemo_Dev"] + TARGETS:
        label = db
        c = existing.get(label)
        if c and c["server"].lower() == SERVER.lower() and c["database"] == db:
            ids[db] = c["id"]
            continue
        assert not c, f"a connection labelled {label} already exists for another server"
        saved = call("POST", "/api/connections", {
            "label": label, "server": SERVER, "database": db, "auth": "windows",
            "encrypt": False, "trustServerCertificate": True, "connectTimeoutSeconds": 15})
        ids[db] = saved["id"]
    d = load()
    d["connections"] = ids
    save(d)
    return ids


def run_compare(ids, targets):
    started = time.perf_counter()
    run = call("POST", "/api/compare", {
        "source": {"connectionId": ids["SSDemo_Dev"]},
        "targets": [{"connectionId": ids[t]} for t in targets]})["runId"]
    while True:
        s = call("GET", f"/api/compare/{run}")
        if s["status"] != "running":
            break
        time.sleep(0.05)
    wall = round((time.perf_counter() - started) * 1000)
    if s["status"] != "done":
        raise RuntimeError(f"compare {run} ended {s['status']}: {s.get('error')}")
    r = s["result"]
    return run, {
        "runId": run, "totalMs": r["totalMs"], "wallMs": wall, "timings": r["timings"],
        "sourceObjects": r["source"]["objectCount"], "sourceReadMs": r["source"]["readMs"],
        "rows": len(r["rows"]),
        "targets": [{k: t.get(k) for k in ("label", "database", "matchPercent", "same", "formattingOnly", "different",
                                       "missingInTarget", "onlyInTarget", "unavailable", "totalObjects", "readMs", "error")}
                    for t in r["targets"]],
    }, r


def compare(n):
    ids = load()["connections"]
    runs = []
    for i in range(n):
        _, summary, _ = run_compare(ids, TARGETS)
        summary["label"] = "cold (first after API start)" if i == 0 else f"warm #{i}"
        runs.append(summary)
        print(summary["label"], summary["totalMs"], "ms", summary["timings"], flush=True)
    single = []
    for i in range(3):
        _, s1, _ = run_compare(ids, [HERO])
        s1["label"] = f"1 target ({HERO}) #{i + 1}"
        single.append(s1)
        print(s1["label"], s1["totalMs"], "ms", flush=True)
    d = load()
    d["compare9"] = runs
    d["compare1"] = single
    save(d)
    for t in runs[-1]["targets"]:
        print(f"{t['label']:10} {t['matchPercent']:6}% same={t['same']} fmt={t['formattingOnly']} diff={t['different']} "
              f"missing={t['missingInTarget']} extra={t['onlyInTarget']}")


def hero():
    ids = load()["connections"]
    run, before, r = run_compare(ids, [HERO])
    print("before", before["targets"][0])
    call("POST", "/api/write-mode", {"enabled": True, "confirm": "ENABLE"})
    try:
        out_dir = str((HERE / "scripts-out").resolve())
        Path(out_dir).mkdir(exist_ok=True)
        scripts = call("POST", f"/api/compare/{run}/scripts", {"targetIndex": 0, "options": {"outputFolder": out_dir}})
        print("scripts:", scripts.get("success"), scripts.get("message"), scripts.get("path"))
        print("validation:", scripts.get("validation"))
        print("held back:", scripts.get("heldBack"))
        sections = scripts["planSections"]
        for s in sections:
            print("  section", s["folder"], s["files"], "default" if s["defaultSelected"] else "opt-in", s["risk"])
        chosen = [s["folder"] for s in sections if s["defaultSelected"]]
        applied = call("POST", f"/api/compare/{run}/apply", {
            "planId": scripts["planId"], "targetIndex": 0, "sections": chosen,
            "confirmDatabase": scripts["applyTarget"]["database"], "progressId": uuid.uuid4().hex})
        print("apply:", applied["success"], applied["message"], "files:", len(applied["applied"]), "stoppedAt:", applied.get("stoppedAt"))
    finally:
        call("DELETE", "/api/write-mode")
    _, after, _ = run_compare(ids, [HERO])
    print("after", after["targets"][0])
    d = load()
    d["hero"] = {"before": before["targets"][0], "after": after["targets"][0], "sections": sections,
                 "appliedSections": chosen, "applySuccess": applied["success"], "applyMessage": applied["message"],
                 "filesApplied": len(applied["applied"]), "heldBack": scripts.get("heldBack"),
                 "scriptsPath": scripts.get("path")}
    save(d)


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "connections":
        print(connections())
    elif cmd == "compare":
        compare(int(sys.argv[2]) if len(sys.argv) > 2 else 5)
    elif cmd == "hero":
        hero()
