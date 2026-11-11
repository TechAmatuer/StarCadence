# Credits and reused work

StarCadence is licensed under the Mozilla Public License 2.0. It builds on these projects:

| Project | Licence | What StarCadence reuses |
|---|---|---|
| [Target Scheduler](https://github.com/tcpalmer/nina.plugin.targetscheduler) N.I.N.A. plug-in, Tom Palmer (commit 8c0c753) | MPL-2.0 | The scoring design (rules returning 0..1, weighted sum, per-rule results kept for explanations); the "setting soonest" rule and the target-switch penalty (here "Slew"); dispatch-style choice of the next target; its CSV bulk-import format. |
| [ExoPlanets](https://github.com/NickHardy/ExoPlanets) N.I.N.A. plug-in, Nick Hardy and contributors (commit 9d7bc2a) | MPL-2.0 | The event window for periodic stars (epoch + period x (n + phase), +/- half-width + O-C range, from `VariableStar.NextEvent`); the column names its CSV parser accepts (`UnifiedVariableStarCsvParser`); the ExoClock field names (`ExoClockTarget`); its three sample CSV files, used as test fixtures. |
| Space Lab (this author's variable-star pipeline) | own work | AAVSO Target Tool field mapping, exposure scaling from a magnitude-12 reference, "familiar" exposure rounding, nearest-filter substitutions. |
| [KStars/Ekos](https://invent.kde.org/education/kstars) | GPL-2.0+ | File formats only (`.esl` scheduler lists, `.esq` capture sequences), read from its source. No code copied. |
| Jean Meeus, *Astronomical Algorithms* (2nd ed.) | - | Sidereal time, precession, Sun (ch. 25), Moon (ch. 47 main terms), Moon illumination (ch. 48). |

Data services: [AAVSO Target Tool](https://targettool.aavso.org) (your own API key), [ExoClock](https://www.exoclock.space).
