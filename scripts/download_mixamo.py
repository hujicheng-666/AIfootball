# -*- coding: utf-8 -*-
"""从 Mixamo 批量下载 Goalkeeper 动作（FBX without skin, 30fps）。
复用 .tmp_edge_profile 中的登录态。"""
import asyncio, json, os, re, sys
from playwright.async_api import async_playwright

OUT = r"D:/AIfootball/data/external/mixamo"
PROFILE = r"D:/AIfootball/.tmp_edge_profile"
PROXY = "http://127.0.0.1:65532"
MANIFEST = os.path.join(OUT, "manifest.json")

CARD_JS = (
    "[...document.querySelectorAll('li')].filter(function(li){"
    "return li.textContent.indexOf('Description:')>=0 && !!(li.offsetWidth||li.offsetHeight)})"
)

def clean_name(title):
    s = re.sub(r"[^A-Za-z0-9]+", "_", title).strip("_")
    return s[:60] if s else "anim"

async def main():
    os.makedirs(OUT, exist_ok=True)
    manifest = {}
    if os.path.exists(MANIFEST):
        manifest = json.load(open(MANIFEST, encoding="utf-8"))
    async with async_playwright() as p:
        ctx = await p.chromium.launch_persistent_context(
            PROFILE, channel="msedge", headless=False,
            proxy={"server": PROXY},
            args=["--disable-blink-features=AutomationControlled"],
            viewport={"width": 1600, "height": 900},
        )
        page = ctx.pages[0] if ctx.pages else await ctx.new_page()
        await page.goto("https://www.mixamo.com/#/?page=1&query=Goalkeeper",
                        wait_until="domcontentloaded")
        # 等卡片加载
        n = 0
        for _ in range(15):
            await page.wait_for_timeout(2000)
            n = await page.evaluate(CARD_JS + ".length")
            if n >= 20:
                break
        print(f"cards={n}", flush=True)
        if n < 20:
            print("ERROR: 卡片加载不足", flush=True)
            await ctx.close()
            sys.exit(1)

        done = set(int(k) for k in manifest.keys())
        for i in range(n):
            if i in done and os.path.exists(manifest[str(i)]["file"]):
                print(f"[{i}] 已存在，跳过", flush=True)
                continue
            try:
                title = await page.evaluate(
                    f"({CARD_JS})[{i}].textContent.trim().split('Description:')[0].trim()")
                await page.evaluate(f"({CARD_JS})[{i}].click()")
                await page.wait_for_timeout(4000)
                # 点击主 Download 按钮（modal 外的）
                await page.evaluate(
                    "[...document.querySelectorAll('button')].filter(function(b){"
                    "return b.textContent.trim().toLowerCase()==='download' "
                    "&& !b.closest('.asset-download-modal');})[0].click()")
                await page.wait_for_selector(".asset-download-modal",
                                             state="visible", timeout=15000)
                await page.wait_for_timeout(800)
                # Skin -> Without Skin（第 2 个 select）
                await page.evaluate(
                    "(function(){var m=document.querySelector('.asset-download-modal');"
                    "var s=m.querySelectorAll('select')[1]; s.value='false';"
                    "s.dispatchEvent(new Event('change',{bubbles:true}));})()")
                await page.wait_for_timeout(600)
                # 确认下载（modal 内最后一个 Download 按钮）
                async with page.expect_download(timeout=180000) as dlinfo:
                    await page.evaluate(
                        "(function(){var m=document.querySelector('.asset-download-modal');"
                        "var bs=[...m.querySelectorAll('button')].filter(function(b){"
                        "return b.textContent.trim()==='Download';});"
                        "bs[bs.length-1].click();})()")
                dl = await dlinfo.value
                fname = f"gk_{i:02d}_{clean_name(title)}.fbx"
                path = os.path.join(OUT, fname)
                await dl.save_as(path)
                manifest[str(i)] = {"title": title, "file": path,
                                    "suggested": dl.suggested_filename}
                json.dump(manifest, open(MANIFEST, "w", encoding="utf-8"),
                          ensure_ascii=False, indent=1)
                print(f"[{i+1}/{n}] OK  {title}  ->  {fname}", flush=True)
            except Exception as e:
                print(f"[{i+1}/{n}] FAIL idx={i}: {e}", flush=True)
                # 关掉可能残留的 modal
                try:
                    await page.keyboard.press("Escape")
                    await page.wait_for_timeout(1000)
                except Exception:
                    pass
        print("ALL DONE", flush=True)
        await ctx.close()

asyncio.run(main())
