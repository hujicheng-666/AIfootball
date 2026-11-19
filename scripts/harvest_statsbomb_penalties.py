"""从 StatsBomb open-data 抓取点球事件，产出可复算的 penalties.jsonl。

为什么要这个脚本：`StatsBombPenaltyImporter` 需要**本地** events 文件，而 open-data 的
events 单个 3–8 MB、整季几百个，中文网络环境下 GitHub raw 基本不可达。这里走 gh-proxy
镜像并发抓取，并且**流式提取**——只留下点球那一行，不把 events 存盘。

用法::

    python_env\\python.exe scripts\\harvest_statsbomb_penalties.py --work %TEMP%\\sb
    python_env\\python.exe scripts\\harvest_statsbomb_penalties.py --work %TEMP%\\sb ^
        --jobs "[[43,3,'WC2018'],[43,106,'WC2022']]"

产物：``<work>/penalties.jsonl``（一行一个点球事件）、``<work>/done.txt``（已处理的
match_id，用于断点续跑）。上游数据许可：CC-BY-SA 4.0 + StatsBomb 署名。
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

MIRROR = "https://gh-proxy.com/https://raw.githubusercontent.com/statsbomb/open-data/master/"

# 已实测可用的组合：赛事 id、赛季 id、标签。
# 注意 StatsBomb 的西甲早期赛季只是抽样（每季 30 余场），凑不出"某门将整个生涯"。
DEFAULT_JOBS = [
    [2, 27, "EPL2015-16"],      # 英超 2015/16 完整全季，380 场
    [43, 3, "WC2018"],          # 2018 世界杯
    [43, 106, "WC2022"],        # 2022 世界杯
    [55, 43, "Euro2020"],       # 2020 欧洲杯
    [55, 282, "Euro2024"],      # 2024 欧洲杯
    [223, 282, "Copa2024"],     # 2024 美洲杯
    [72, 30, "WWC2019"],        # 2019 女足世界杯
    [72, 107, "WWC2023"],       # 2023 女足世界杯
]


def fetch(path: str, tries: int = 3, timeout: int = 180) -> bytes:
    last: Exception | None = None
    for attempt in range(tries):
        try:
            request = urllib.request.Request(MIRROR + path, headers={"User-Agent": "Mozilla/5.0"})
            return urllib.request.urlopen(request, timeout=timeout).read()
        except Exception as error:  # noqa: BLE001 - 网络错误种类多，统一重试
            last = error
            time.sleep(1.5 * (attempt + 1))
    assert last is not None
    raise last


def starting_keepers(events: list[dict]) -> dict[str, str | None]:
    """从 Starting XI 的 tactics.lineup 里取每队首发门将。

    Shot 事件本身**不带**门将字段，只能这样反推。
    """
    keepers: dict[str, str | None] = {}
    for event in events:
        if event.get("type", {}).get("name") != "Starting XI":
            continue
        team = event.get("team", {}).get("name")
        if team is None:
            continue
        for player in event.get("tactics", {}).get("lineup", []):
            if player.get("position", {}).get("name") == "Goalkeeper":
                keepers[team] = player.get("player", {}).get("name")
        keepers.setdefault(team, None)
    return keepers


def parse_match(match_id: int) -> tuple[int, list[dict]]:
    events = json.loads(fetch(f"data/events/{match_id}.json"))
    keepers = starting_keepers(events)
    rows: list[dict] = []
    for event in events:
        # 关键：点球的**事件类型**是 Shot，`shot.type` 才是 Penalty。
        if event.get("type", {}).get("name") != "Shot":
            continue
        shot = event.get("shot", {})
        if (shot.get("type") or {}).get("name") != "Penalty":
            continue

        shooter_team = event.get("team", {}).get("name")
        keeper = next((name for team, name in keepers.items() if team != shooter_team), None)
        location = shot.get("end_location")
        row: dict = {
            "match": match_id,
            "minute": event.get("minute"),
            "period": event.get("period"),
            "team": shooter_team,
            "shooter": (event.get("player") or {}).get("name"),
            "keeper": keeper,
            "outcome": shot.get("outcome", {}).get("name"),
            "body_part": shot.get("body_part", {}).get("name"),
            "technique": shot.get("technique", {}).get("name"),
        }
        if location and len(location) >= 2:
            row["end_x"] = location[0]
            row["end_y"] = location[1]
            row["end_z"] = location[2] if len(location) > 2 else None
        else:
            row["missing"] = True
        rows.append(row)
    return match_id, rows


def harvest(work: str, jobs: list[list], workers: int) -> None:
    os.makedirs(work, exist_ok=True)
    penalties_path = os.path.join(work, "penalties.jsonl")
    done_path = os.path.join(work, "done.txt")
    done = set(open(done_path, encoding="utf-8").read().split()) if os.path.exists(done_path) else set()

    for competition, season, label in jobs:
        matches = json.loads(fetch(f"data/matches/{competition}/{season}.json"))
        ids = [match["match_id"] for match in matches]
        pending = [match_id for match_id in ids if str(match_id) not in done]
        print(f"[{label}] {len(ids)} matches, {len(pending)} pending", flush=True)

        started = time.time()
        found = 0
        with ThreadPoolExecutor(max_workers=workers) as pool, \
                open(penalties_path, "a", encoding="utf-8") as penalties_out, \
                open(done_path, "a", encoding="utf-8") as done_out:
            futures = {pool.submit(parse_match, match_id): match_id for match_id in pending}
            for index, future in enumerate(futures, 1):
                try:
                    match_id, rows = future.result()
                except Exception as error:  # noqa: BLE001 - 单场失败不应中断整批
                    print(f"  FAIL {futures[future]}: {error}", flush=True)
                    continue
                for row in rows:
                    penalties_out.write(json.dumps(row, ensure_ascii=False) + "\n")
                penalties_out.flush()
                done_out.write(f"{match_id}\n")
                done_out.flush()
                found += len(rows)
                if index % 40 == 0:
                    print(f"  {index}/{len(pending)} matches, {found} penalties, {time.time() - started:.0f}s",
                          flush=True)
        print(f"[{label}] DONE {len(pending)} matches, {found} penalties, {time.time() - started:.0f}s",
              flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="抓取 StatsBomb open-data 的点球事件")
    parser.add_argument("--work", required=True, help="输出目录（放 penalties.jsonl / done.txt）")
    parser.add_argument("--jobs", default=None,
                        help='JSON 数组，形如 [[43,3,"WC2018"]]；省略则跑内置全部赛事')
    parser.add_argument("--workers", type=int, default=16, help="并发数，默认 16")
    args = parser.parse_args()

    jobs = json.loads(args.jobs) if args.jobs else DEFAULT_JOBS
    harvest(args.work, jobs, args.workers)
    return 0


if __name__ == "__main__":
    sys.exit(main())
