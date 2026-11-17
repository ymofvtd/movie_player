"""Fetch community timings for an existing MyCinema library; never change videos or the DB.

Uses IMDb's title suggestions for exact title/year/type matching and IntroDB's public
read API. Existing timing files are preserved. Output includes raw responses and a
per-file audit report. Requires Python 3 and the installed MyCinema VLC runtime.
"""
import argparse
import ctypes
import json
import os
from pathlib import Path
import re
import sqlite3
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request


def normalized(value):
    return re.sub(r"[^a-z0-9]", "", unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode().lower().replace("&", "and"))


def fetch(url, cache):
    if cache.exists():
        return json.loads(cache.read_text(encoding="utf-8"))
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "MyCinema-TimingImport/1.0", "Accept": "application/json"})
            with urllib.request.urlopen(request, timeout=20) as response:
                result = json.load(response)
            cache.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
            return result
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return {}
            if error.code != 429 and error.code < 500:
                raise
        except (TimeoutError, urllib.error.URLError):
            if attempt == 2:
                raise
        time.sleep(2 * (attempt + 1))
    raise RuntimeError("Service unavailable after retries")


class RuntimeReader:
    def __init__(self, directory):
        self.dll_directory = os.add_dll_directory(str(directory))
        self.vlc = ctypes.CDLL(str(directory / "libvlc.dll"))
        functions = {
            "libvlc_new": ([ctypes.c_int, ctypes.POINTER(ctypes.c_char_p)], ctypes.c_void_p),
            "libvlc_media_new_path": ([ctypes.c_void_p, ctypes.c_char_p], ctypes.c_void_p),
            "libvlc_media_parse_with_options": ([ctypes.c_void_p, ctypes.c_int, ctypes.c_int], ctypes.c_int),
            "libvlc_media_get_parsed_status": ([ctypes.c_void_p], ctypes.c_int),
            "libvlc_media_get_duration": ([ctypes.c_void_p], ctypes.c_longlong),
            "libvlc_media_release": ([ctypes.c_void_p], None),
            "libvlc_release": ([ctypes.c_void_p], None),
        }
        for name, (args, result) in functions.items():
            function = getattr(self.vlc, name)
            function.argtypes, function.restype = args, result
        args = (ctypes.c_char_p * 2)(b"--quiet", b"--no-metadata-network-access")
        self.instance = self.vlc.libvlc_new(2, args)
        if not self.instance:
            raise RuntimeError("Could not initialize local VLC metadata reader")

    def read(self, path):
        media = self.vlc.libvlc_media_new_path(self.instance, str(path).encode("utf-8"))
        if not media:
            return 0
        try:
            if self.vlc.libvlc_media_parse_with_options(media, 0, 7000) != 0:
                return 0
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                status = self.vlc.libvlc_media_get_parsed_status(media)
                if status:
                    return max(0, self.vlc.libvlc_media_get_duration(media)) if status == 4 else 0
                time.sleep(0.05)
            return 0
        finally:
            self.vlc.libvlc_media_release(media)

    def close(self):
        self.vlc.libvlc_release(self.instance)
        self.dll_directory.close()


def clock(milliseconds):
    seconds, fraction = divmod(milliseconds, 1000)
    hours, seconds = divmod(seconds, 3600)
    minutes, seconds = divmod(seconds, 60)
    return f"{hours:02}:{minutes:02}:{seconds:02}.{fraction:03}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--database", type=Path, default=Path(os.environ["LOCALAPPDATA"]) / "MyCinema/Data/library.db")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--apply", action="store_true", help="Create timing files; otherwise only write a review report")
    parser.add_argument("--unscanned-only", action="store_true", help="Check numbered episode filenames on disk that are not yet catalogued")
    parser.add_argument("--title-map", type=Path, help="JSON list of verified title/year/IMDb/source overrides")
    options = parser.parse_args()
    options.root = options.root.resolve(strict=True)
    options.output.mkdir(parents=True, exist_ok=True)
    cache = options.output / "responses"
    cache.mkdir(exist_ok=True)
    with sqlite3.connect(options.database.resolve().as_uri() + "?mode=ro", uri=True) as connection:
        connection.row_factory = sqlite3.Row
        items = {r["Id"]: dict(r) for r in connection.execute("SELECT * FROM LibraryItems")}
        files = [dict(r) for r in connection.execute("SELECT * FROM MediaFiles WHERE IsAvailable=1")]

    title_map = json.loads(options.title_map.read_text(encoding="utf-8")) if options.title_map else []
    if options.unscanned_only:
        known = {str(Path(f["FilePath"]).resolve()).lower() for f in files}
        files = []
        shows = {}
        for path in options.root.rglob("*"):
            if path.suffix.lower() not in {".mkv", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts"}:
                continue
            if str(path.resolve()).lower() in known or any(p.lower() in {"extras", "trailers", "featurettes", "samples"} for p in path.parts):
                continue
            match = re.match(r"^(.+?)(?:\s*-\s*|\s+)\(?(\d{4})\)?\s*-\s*S(\d+)E(\d+)(?:-E?(\d+))?", path.stem, re.I)
            if not match:
                continue
            name, year, season, episode, end = match.groups()
            key = (name.strip(), int(year))
            if key not in shows:
                show_id = -len(items) - 1
                items[show_id] = {"Id": show_id, "Title": key[0], "ReleaseYear": key[1], "ItemType": 3}
                shows[key] = show_id
            season_id = -len(items) - 1
            items[season_id] = {"Id": season_id, "ParentId": shows[key]}
            item_id = -len(items) - 1
            items[item_id] = {"Id": item_id, "ParentId": season_id, "ItemType": 5,
                              "SeasonNumber": int(season), "EpisodeNumber": int(episode), "EpisodeEndNumber": int(end) if end else None}
            files.append({"LibraryItemId": item_id, "FilePath": str(path)})

    # Keep the original library DB read-only. Runtimes and title matches live in the audit directory.
    runtime_path = options.output / "runtimes.json"
    runtimes = json.loads(runtime_path.read_text(encoding="utf-8")) if runtime_path.exists() else {}
    runtime_reader = RuntimeReader(Path(os.environ["LOCALAPPDATA"]) / "Programs/MyCinema/libvlc/win-x64")
    identities = {}
    report = []
    try:
        for index, file in enumerate(files):
            path = Path(file["FilePath"])
            entry = {"video": str(path), "status": "pending"}
            report.append(entry)
            try:
                if not path.resolve().is_relative_to(options.root) or not path.is_file() or path.is_symlink():
                    entry["status"] = "outside root or unavailable"
                    continue
                item = items[file["LibraryItemId"]]
                is_movie = item["ItemType"] == 2
                if not is_movie and item["ItemType"] != 5:
                    entry["status"] = "unidentified media"
                    continue
                title = item if is_movie else items[items[item["ParentId"]]["ParentId"]]
                entry["title"] = title["Title"]
                if title["Id"] not in identities:
                    overrides = [m for m in title_map if m["title"] == title["Title"] and m["year"] == title["ReleaseYear"]
                                 and m["type"] == ("movie" if is_movie else "tv") and m.get("source")]
                    if len(overrides) == 1:
                        identities[title["Id"]] = overrides[0]["imdb_id"]
                    else:
                        url = "https://v3.sg.media-imdb.com/suggestion/x/" + urllib.parse.quote(title["Title"].lower(), safe="") + ".json"
                        data = fetch(url, cache / f"identity-{title['Id']}.json")
                        matches = [d for d in data.get("d", []) if normalized(d.get("l", "")) == normalized(title["Title"])
                                   and d.get("y") == title["ReleaseYear"] and d.get("qid") in
                                   (("movie", "tvMovie", "tvSpecial") if is_movie else ("tvSeries", "tvMiniSeries"))]
                        identities[title["Id"]] = matches[0]["id"] if len(matches) == 1 else None
                    print(f"MATCH {title['Title']!r}: {identities[title['Id']]}", flush=True)
                imdb = identities[title["Id"]]
                if not imdb:
                    entry["status"] = "no unambiguous title/year/type match"
                    continue
                entry["imdb_id"] = imdb
                entry["identity_source"] = f"https://www.imdb.com/title/{imdb}/"
                if not is_movie and (not item["SeasonNumber"] or not item["EpisodeNumber"]):
                    entry["status"] = "specials require manual episode mapping"
                    continue
                if not is_movie and item.get("EpisodeEndNumber") and item["EpisodeEndNumber"] != item["EpisodeNumber"]:
                    entry["status"] = "combined episodes require file-specific manual timings"
                    continue
                params = {"imdb_id": imdb}
                if is_movie:
                    params["is_movie"] = "true"
                else:
                    params.update(season=item["SeasonNumber"], episode=item["EpisodeNumber"])
                url = "https://api.introdb.app/segments?" + urllib.parse.urlencode(params)
                entry["timing_source"] = url
                data = fetch(url, cache / (imdb + ("-movie" if is_movie else f"-s{item['SeasonNumber']}e{item['EpisodeNumber']}") + ".json"))
                segments = [(kind, data.get(kind)) for kind in ("intro", "recap", "outro") if data.get(kind)]
                if not segments:
                    entry["status"] = "no community timings available"
                    continue
                stamp = f"{path.stat().st_size}:{path.stat().st_mtime_ns}"
                old = runtimes.get(str(path), {})
                duration = old.get("duration_ms", 0) if old.get("stamp") == stamp else 0
                if not duration:
                    duration = runtime_reader.read(path)
                    runtimes[str(path)] = {"stamp": stamp, "duration_ms": duration}
                    runtime_path.write_text(json.dumps(runtimes, indent=2), encoding="utf-8")
                entry["duration_ms"] = duration
                if duration <= 0:
                    entry["status"] = "could not verify video runtime"
                    continue
                accepted, rejected = [], []
                for kind, segment in segments:
                    start, end = segment["start_ms"], segment["end_ms"]
                    if kind == "outro" and data.get("post_credits"):
                        scene_start = data["post_credits"]["start_ms"]
                        if start < scene_start < end:
                            end = scene_start
                    if duration < end <= duration + 3000:
                        end = duration
                    valid = 0 <= start < end <= duration and segment.get("confidence", 0) >= 0.7
                    valid &= (start >= duration * 0.7) if kind == "outro" else (end <= min(duration * 0.5, 600000))
                    (accepted if valid else rejected).append({"kind": kind, "start_ms": start, "end_ms": end})
                entry["segments"], entry["rejected_segments"] = accepted, rejected
                if not accepted:
                    entry["status"] = "timings did not pass runtime/confidence checks"
                    continue
                sidecar = path.with_suffix(".skip.txt")
                # Distinct extensions with a shared basename must not share imported timings.
                if sum(Path(f["FilePath"]).with_suffix(".skip.txt") == sidecar for f in files) > 1:
                    sidecar = Path(str(path) + ".skip.txt")
                entry["sidecar"] = str(sidecar)
                if sidecar.exists() or Path(str(path) + ".skip.txt").exists():
                    entry["status"] = "preserved existing timing file"
                    continue
                lines = ["# MyCinema community skip timings", "# Contains data from IntroDB (introdb.app), available under the ODbL.",
                         "# Source: " + url, "# Identity: " + entry["identity_source"],
                         "# Ranges checked against local runtime; release offsets have not been visually verified.",
                         "# Edit these values or use intro=none / outro=none to disable a segment."]
                lines += [f"{s['kind']}={clock(s['start_ms'])}-{clock(s['end_ms'])}" for s in accepted]
                if options.apply:
                    with sidecar.open("x", encoding="utf-8", newline="\n") as stream:
                        stream.write("\n".join(lines) + "\n")
                entry["status"] = "created" if options.apply else "ready"
            except Exception as error:
                entry["status"] = "error"
                entry["error"] = str(error)
            finally:
                (options.output / "import-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                if index % 20 == 0:
                    print(f"Checked {index + 1}/{len(files)} files", flush=True)
    finally:
        runtime_reader.close()
    from collections import Counter
    print(json.dumps(dict(Counter(r["status"] for r in report)), indent=2), flush=True)


if __name__ == "__main__":
    main()
