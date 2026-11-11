"""End-to-end test of the app window in a real browser (headless Chromium via Playwright).

Usage: python tests/ui/app_ui_test.py http://127.0.0.1:PORT/ [screenshot-folder]
The app must be running with the example data (see .github/workflows/ci.yml).
"""
import asyncio, os, sys
from playwright.async_api import async_playwright

URL = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8799/"
SHOTS = sys.argv[2] if len(sys.argv) > 2 else None

async def main():
    errors, failures = [], []
    def check(ok, what):
        print(("ok   " if ok else "FAIL ") + what)
        if not ok: failures.append(what)
    async with async_playwright() as p:
        exe = os.environ.get("CHROME")
        b = await p.chromium.launch(**({"executable_path": exe} if exe else {}), args=["--no-sandbox"])
        pg = await b.new_page(viewport={"width": 1280, "height": 900})
        pg.on("pageerror", lambda e: errors.append(str(e)))
        await pg.goto(URL)
        await pg.wait_for_selector("#tab-tonight .card")
        check("StarCadence" in await pg.title(), "page loads")
        await pg.click("nav button[data-tab=tonight]")
        await pg.fill("#date", "2026-12-03"); await pg.dispatch_event("#date", "change")
        await pg.click("#planbtn")
        await pg.wait_for_selector("svg.tl", timeout=30000)
        rows = await pg.locator("#tab-tonight table").first.locator("tbody tr").count()
        check(rows >= 5, f"plan table has rows ({rows})")
        check(await pg.locator("svg.tl rect").count() > rows, "timeline drawn")
        links = await pg.locator("a.dl").count()
        check(links == 5, f"five download buttons ({links})")
        async with pg.expect_download() as d:
            await pg.click("a.dl >> nth=0")
        check((await d.value).suggested_filename.endswith(".csv"), "N.I.N.A. file downloads")
        async with pg.expect_download() as d:
            await pg.click("a.dl >> nth=1")
        check((await d.value).suggested_filename.endswith(".zip"), "Ekos zip downloads")
        if SHOTS: await pg.screenshot(path=os.path.join(SHOTS, "tonight.png"))
        await pg.click("nav button[data-tab=targets]")
        await pg.click("#addwatch")
        n = await pg.locator("[data-rmwatch]").count()
        await pg.fill(f'input[data-p="watchList.{n-1}.name"]', "RS Oph")
        await pg.fill(f'input[data-p="watchList.{n-1}.ra"]', "17 50 13.2")
        await pg.fill(f'input[data-p="watchList.{n-1}.dec"]', "-06 42 28")
        await pg.click("#savet"); await pg.wait_for_timeout(800)
        names = await pg.evaluate("S.state.config.watchList.map(w => w.name)")
        check("RS Oph" in names, "watch-list star saved")
        await pg.click("nav button[data-tab=setup]")
        await pg.fill('input[data-p="site.latitude"]', "123")
        await pg.click("#saves"); await pg.wait_for_timeout(800)
        check("latitude" in (await pg.text_content("#toast") or ""), "bad latitude refused with a message")
        await pg.fill('input[data-p="site.latitude"]', "40")
        await pg.click("#saves"); await pg.wait_for_timeout(800)
        check(not await pg.evaluate("S.dirty"), "good settings saved")
        await b.close()
    check(not errors, f"no script errors {errors}")
    sys.exit(1 if failures else 0)

asyncio.run(main())
