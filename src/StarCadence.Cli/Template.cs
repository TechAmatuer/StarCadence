// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

static class Template {
    public const string ConfigJson = """
{
  // StarCadence configuration. Comments are allowed. Times are UTC.
  "site": {
    "name": "My observatory",
    "latitude": 40.0,            // degrees, north positive
    "longitude": -75.0,          // degrees, EAST positive (west is negative)
    "elevationM": 100,
    "timeZone": "America/New_York",   // for local times in reports (IANA name works on Windows and macOS)
    "minAltitude": 30,
    "sunAltitude": -12,          // -12 nautical twilight, -18 astronomical
    "moonSeparation": 15,
    "maxAirmass": 2.5,
    "horizon": []                // optional: [{ "az": 0, "alt": 20 }, { "az": 90, "alt": 35 }, ...]
  },

  // One entry per filter. Exposure for a star of magnitude m = refExposure x 10^(0.4 (m - refMag)),
  // kept between minExposure and maxExposure; fainter stars get extra frames (up to maxCount).
  // colourOffset: typical (band - V) for stars with no magnitude in this band.
  "filters": [
    { "name": "B", "aavso": "B", "refMag": 12, "refExposure": 40, "minExposure": 2, "maxExposure": 300, "count": 1, "maxCount": 6, "colourOffset": 0.6 },
    { "name": "V", "aavso": "V", "refMag": 12, "refExposure": 20, "minExposure": 1, "maxExposure": 300, "count": 1, "maxCount": 6, "colourOffset": 0.0 },
    { "name": "R", "aavso": "R", "refMag": 12, "refExposure": 15, "minExposure": 1, "maxExposure": 300, "count": 1, "maxCount": 6, "colourOffset": -0.4 },
    { "name": "I", "aavso": "I", "refMag": 12, "refExposure": 20, "minExposure": 1, "maxExposure": 300, "count": 1, "maxCount": 6, "colourOffset": -0.8 }
  ],

  "overheads": { "slewAndCenterS": 90, "filterChangeS": 8, "readoutS": 3 },

  "planner": {
    "stepMinutes": 2,
    "maxTargets": 300,
    "dueFraction": 0.8,          // due again after 80% of the cadence
    "defaultCadenceDays": 1,
    "defaultSeriesMinutes": 60,
    "transitBaselineMinutes": 60,
    "transitFilter": "R",
    "eventHalfWidthMinutes": 60,
    "ruleWeights": { "Priority": 0.5, "Cadence": 1.0, "LowCoverage": 0.5, "Alert": 0.8, "SettingSoonest": 0.5, "Deadline": 0.4, "Slew": 0.3, "Altitude": 0.2, "Efficiency": 0.3 }
  },

  "sources": {
    // AAVSO Target Tool: put YOUR key in the environment variable below (or in a file named by targetToolKeyFile).
    "targetToolKeyVariable": "AAVSO_TARGETTOOL_KEY",
    "targetToolSections": [ "ac" ],      // ac = alerts/campaigns; also ep, cv, eb, spp, lpv, yso, het, misc
    "targetToolCsv": null,               // or a CSV exported from the Target Tool website
    "csvFiles": [],                      // more lists: any CSV with Name, RA, Dec (ExoPlanets plug-in formats work)
    "exoClock": false,                   // true = add ExoClock transit targets
    "exoClockPriorities": [ "alert", "high", "medium" ],
    "exoClockMaxMag": 13.5,
    "exoClockMinDepthMmag": 5,
    "cacheDir": "cache"
  },

  // Stars you always want. Windows: "start/end" ISO times; prefix "fixed:" when the whole window must be covered.
  // For eclipsing binaries give epoch (HJD) and period (days); the planner finds each minimum in the night.
  "watchList": [
    { "name": "SS Cyg", "ra": "21 42 42.8", "dec": "+43 35 09.9", "mag": 11.5, "magBright": 8.0, "cadenceDays": 1, "priority": 0.8, "filters": [ "V", "B" ] }
    // an eclipsing binary: { "name": "...", "ra": "...", "dec": "...", "mag": 11, "epoch": 2460000.123, "period": 1.2345, "eventMinutes": 90 }
  ],

  "export": {
    "ekosSequenceDir": null,             // folder where the .esq files will be on the Ekos computer (absolute)
    "ekosImageDir": null,
    "ekosProfile": "Default",
    "ekosSteps": [ "Track", "Align" ]
  },

  "historyFile": "history.json"
}
""";
}
