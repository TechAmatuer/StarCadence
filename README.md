# StarCadence

[![build and test](https://github.com/TechAmatuer/StarCadence/actions/workflows/ci.yml/badge.svg)](https://github.com/TechAmatuer/StarCadence/actions/workflows/ci.yml)

**StarCadence plans a whole night of variable-star and exoplanet-transit photometry for an automated observatory.
It runs on Windows, macOS and Linux.**

You tell it four things: where your observatory is, which filters you have, how long a 12th-magnitude star needs in
each filter, and where your targets come from (the AAVSO Target Tool, ExoClock, CSV lists, your own watch list).
For each night it works out:

- **which stars to observe, and in what order**: alert and campaign stars, stars that are overdue or get few
  observations, and stars that are about to set come first;
- **when**: transits and eclipse minima at their exact times, other stars when they are due, high enough and away from
  the Moon;
- **how**: which filters, how many seconds per exposure for that star's brightness, and how many frames.

It writes the plan as text, CSV and JSON, plus files your observatory software can load (KStars/Ekos), or import
(N.I.N.A. Target Scheduler).

It does **not** move the telescope, take or process images, or submit anything to the AAVSO. It plans the night;
your observatory software runs it; you send the results.

## Why

Observers with automated observatories still pick targets by hand: which alert or campaign stars need data, which are
overdue, which time-series and transit windows fit tonight, and what exposure each star needs in each filter.
With tens of targets and four filters, that is an hour of work every day. StarCadence does it in about a second, and
says why it chose each star and why it left others out.

## What a plan looks like

From [`examples/sample-output/plan.txt`](examples/sample-output/plan.txt) (sample site and targets, night of 3 December 2026):

```
Night of 2026-12-03 at My observatory  (times America/New_York)
Dark from 17:38 to 06:01; Moon 21% lit
14 blocks, 14 targets from 18 candidates; 41% of the dark time used

17:38-17:41  V Sge                  Variable  alt 57->57  B 1x40s  V 1x10s  R 1x5s  I 1x5s
      score 3.30 (Cadence 1.00, Alert 0.70, Priority 1.00)
      - saturates if brighter than B 10.8, V 10.2, R 9.8, I 9.4 (catalogue maximum 8.6): re-plan after an outburst alert
      - AAVSO alert or campaign target
17:41-17:44  SS Cyg                 Variable  alt 80->80  B 1x40s  V 1x12s
      score 3.23 (Cadence 0.99, Alert 0.70, Priority 1.00)
...
23:33-04:02  Sample-1b              Transit  [fixed]  alt 81->48  R 1463x8s
      score 1.85 (Priority 0.80, LowCoverage 0.75, Slew 1.00); transit mid 06:48 UTC

Not planned (4):
  T CrB: not observable tonight (too low)
  Sample-2b: transit mid 10:40 UTC: window runs outside dark time
```

## The four main features

### 1. Time windows
- **Transits** (from ExoClock or your list): it predicts every transit in the night from the epoch and period. Each
  window runs from ingress minus a baseline to egress plus a baseline (60 minutes by default). The window is widened by
  the ephemeris uncertainty for that date, and the BJD time is converted to UTC.
- **Eclipse minima and other periodic events**: give an epoch and a period (and a phase, if it isn't the primary
  minimum); it finds each event in the night.
- **Your own windows**: any target can have time windows. A *fixed* window must be covered start to end; an ordinary
  one only limits when the star may be observed. Campaign start and end dates are honoured too.
- A fixed window is planned only if the target is observable for all of it. When two fixed windows clash, the
  higher-scoring one is kept and the other is reported.

### 2. Repeat cadence
- Each star has a cadence in days (from the Target Tool, your list, or a default). It is due again once 80% of the
  cadence has passed since your last visit.
- A cadence under a day means several visits in one night. For example, 0.1 days gives a visit about every 2.4 hours
  while the star is up.
- Your visits are kept in `history.json`. Mark a night done with `starcadence record`, so stars are not repeated too soon.

### 3. Target sources
- **AAVSO Target Tool**, through its API with your own key, or a CSV exported from its website. It reads alerts and
  campaigns, cadence, filters, priority, last data point, time-series requests and solar conjunction. Stars that want
  only spectroscopy are skipped.
- **ExoClock** transit planets, filtered by priority, brightness and depth.
- **Any CSV list** with Name, RA and Dec columns, in any order. The column names used by the N.I.N.A. ExoPlanets
  plug-in all work.
- **Watch list** in the configuration, for stars you always want.
- The same star from several lists or rows is merged into one target. It keeps the highest priority, the shortest
  cadence and every filter asked for.

### 4. Exposure per star brightness and filter
- Each filter has a reference exposure for a magnitude-12 star. A star of magnitude *m* gets
  `refExposure x 10^(0.4 (m - 12))`, rounded to a familiar value and kept between your shortest and longest exposure.
- A star too faint for the longest exposure gets extra frames, up to a limit you set.
- A star with no magnitude in a filter's band uses its V magnitude plus a typical colour offset for that filter.
- If a star's catalogue maximum would saturate at the planned exposure (a dwarf nova in outburst, say), the plan
  says at what magnitude each filter saturates. You can also make it shorten exposures for the bright limit
  (`planner.capForBrightLimit`).
- A filter that isn't in your wheel is replaced by the nearest one (Sloan r by R, for example), and the plan says so.

## How it decides

1. Fixed windows (transits, minima) go in first, best score first, with no overlaps.
2. The gaps are filled one visit at a time. At each moment it takes the highest-scoring star that is due, inside its
   windows and dates, and observable for its whole visit. The visit includes the slew, filter changes, exposures and
   readouts.
3. The score adds up weighted rules (the same design as N.I.N.A. Target Scheduler). Every weight can be changed in
   `planner.ruleWeights`:

| Rule | Prefers |
|---|---|
| Priority | targets marked high priority (Target Tool, ExoClock, watch list) |
| Cadence | stars overdue for their cadence (up to 6 cadences late) |
| LowCoverage | stars with few recent observations |
| Alert | AAVSO alert and campaign targets |
| SettingSoonest | targets about to set |
| Deadline | campaigns about to end |
| Slew | short slews from the last target |
| Altitude | higher in the sky |
| Efficiency | short visits, so more stars fit |

Running the planner again during the night (`--from now`) plans the rest of the night: after clouds, a fault, or a
new alert.

## Outputs

| File | For |
|---|---|
| `plan.txt` | reading: times, filters, exposures, reasons |
| `plan.csv` | a spreadsheet: one row per filter step |
| `plan.json` | other programs (the full plan) |
| `ekos/plan.esl` + `.esq` files | **KStars/Ekos** (macOS, Linux, Windows). Transits and minima start and stop at their times; other jobs run in plan order. |
| `nina-target-scheduler.csv` | **N.I.N.A. Target Scheduler** bulk import (Windows), one row per target, typed by exposure length so each type maps to an exposure template. Target Scheduler has no time windows, so transits and minima are left out and listed on screen. |

## Quick start

**1. Install the .NET SDK (version 8 or newer), once.**

Mac (Terminal):
```bash
brew install --cask dotnet-sdk          # or the installer from https://dotnet.microsoft.com/download
```
Windows (PowerShell):
```powershell
winget install Microsoft.DotNet.SDK.8   # or the installer from https://dotnet.microsoft.com/download
```

**2. Get StarCadence and try the example night.**
```bash
git clone https://github.com/TechAmatuer/StarCadence.git
cd StarCadence
dotnet run --project src/StarCadence.Cli -c Release -- plan --config examples/config.json --date 2026-12-03 --out my-test
```

**3. Make your own configuration.**
```bash
dotnet run --project src/StarCadence.Cli -c Release -- init --dir my-observatory
```
Edit `my-observatory/config.json`: your site's latitude, longitude (east positive) and time zone, your filters and
reference exposures, and your sources. Then:
```bash
dotnet run --project src/StarCadence.Cli -c Release -- plan --config my-observatory/config.json --formats txt,csv,ekos,nina
```

Ready-made programs for Windows, Mac (Apple Silicon) and Linux are built on every change: open the latest run under
[Actions](https://github.com/TechAmatuer/StarCadence/actions) and download `starcadence-win-x64`,
`starcadence-osx-arm64` or `starcadence-linux-x64`. They need no .NET install. On a Mac, after unzipping, run
`chmod +x starcadence && xattr -d com.apple.quarantine starcadence` once in that folder (downloads lose the
"executable" mark, and macOS blocks unsigned downloaded programs until told otherwise).

## Commands

```
starcadence init      [--dir FOLDER]                   write an example config.json to edit
starcadence fetch     [--config FILE]                  download AAVSO Target Tool and ExoClock data
starcadence plan      [--config FILE] [--date YYYY-MM-DD] [--from now|TIME]
                      [--out FOLDER] [--formats txt,json,csv,ekos,nina] [--fetch]
starcadence record    [--config FILE] --target NAME [--utc TIME]
                      or --plan plan.json [--until TIME]   mark visits done (drives the cadence)
starcadence exposures [--config FILE] --mag M [--bright M]  exposure per filter for one star
starcadence ephem     [--config FILE] --utc TIME [--ra RA --dec DEC]   Sun, Moon and target positions
```

Without `--date`, `plan` plans tonight (or the night in progress). Times are UTC, written like `2026-12-04T03:00:00Z`.
RA can be `05 34 31.9`, `05:34:31.9` or decimal degrees (use a column named `RA_h` for decimal hours).

### Your AAVSO Target Tool key

StarCadence never stores the key. Put it in an environment variable before `fetch`:
```bash
export AAVSO_TARGETTOOL_KEY="your key"     # Mac/Linux
$env:AAVSO_TARGETTOOL_KEY = "your key"     # Windows PowerShell
```
Without a key, export a CSV from the Target Tool website and set `sources.targetToolCsv`.

## Accuracy and testing

- **Positions** of the Sun, Moon and stars use Meeus's algorithms. Checked against astropy at 40 random times:
  altitudes within 0.013°, Moon separation within 0.006°, Moon illumination within 0.002.
- **Dark-time limits** fall where the Sun really is at the limit altitude (within 0.005°).
- **Transit and minimum times** convert BJD/HJD to UTC within 6 seconds.
- **35 automated tests** check the planner's rules: no overlaps, every block observable, windows kept, cadence spacing,
  sources, exporters, and number formats under other locales. GitHub Actions runs them on Windows, macOS (Apple
  Silicon) and Linux on every change (badge at the top).
- **File formats**: Ekos files follow the KStars source; the Target Scheduler CSV is read back the way its importer
  reads it.

**Not yet tested:** loading the output into a live Ekos or N.I.N.A. session; live Target Tool and ExoClock downloads.
Please open an issue if something doesn't load.

## Known limits

- Meridian flips are not modelled; your observatory software handles them during a visit.
- Exposures come from the magnitude you give (or the middle of the catalogue range). Give current magnitudes for
  large-amplitude stars (Miras, novae) or the exposure can be far off.
- The N.I.N.A. Target Scheduler file can't carry time windows or cadence. A N.I.N.A. plug-in that runs StarCadence
  plans directly is the next step.

## Licence and credits

[MPL-2.0](LICENSE.txt). StarCadence reuses ideas and code from the N.I.N.A. **Target Scheduler** plug-in (scoring
rules) and the **ExoPlanets** plug-in (event windows, CSV formats, ExoClock fields), both MPL-2.0. See
[NOTICE.md](NOTICE.md).
