// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// N.I.N.A. reads these attributes to describe the plug-in (its plugin manager and the manifest tool use them).

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: Guid("7D9D9D1E-5C3B-4C2F-9A9E-2B3F6E0C5A11")]
[assembly: AssemblyTitle("StarCadence")]
[assembly: AssemblyDescription("Runs a StarCadence night plan inside N.I.N.A.: variable stars, transits and eclipse minima with time windows and repeat cadence, re-planned after every target.")]
[assembly: AssemblyCompany("StarCadence contributors")]
[assembly: AssemblyProduct("StarCadence")]
[assembly: AssemblyCopyright("MPL-2.0")]
[assembly: AssemblyVersion("0.4.0.0")]
[assembly: AssemblyFileVersion("0.4.0.0")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Repository", "https://github.com/TechAmatuer/StarCadence")]
[assembly: AssemblyMetadata("Homepage", "https://github.com/TechAmatuer/StarCadence")]
[assembly: AssemblyMetadata("Tags", "Variable stars,Exoplanets,AAVSO,Scheduling,Photometry")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/TechAmatuer/StarCadence/releases")]
[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/TechAmatuer/StarCadence/main/assets/starcadence.png")]
[assembly: AssemblyMetadata("ScreenshotURL", "https://raw.githubusercontent.com/TechAmatuer/StarCadence/main/docs/screenshot-tonight.png")]
[assembly: AssemblyMetadata("LongDescription", @"StarCadence plans a whole night of variable-star and exoplanet-transit photometry and this plug-in runs it inside N.I.N.A.

Add the **StarCadence Night** instruction to a sequence (after your usual unpark, cool and autofocus steps). When it runs it:
* plans the rest of the night from your StarCadence settings and target lists (AAVSO Target Tool, ExoClock, CSV lists, watch list),
* slews to the first target (plate-solve centring when enabled), sets the filter, takes the exposures StarCadence worked out for that star's brightness,
* records the visit, plans again, and goes on until dawn,
* keeps transits and eclipse minima to their time windows and repeats stars on their cadence.

Set up StarCadence first (double-click StarCadence.exe: observatory, filters, targets). The plug-in reads the same settings folder (Documents\StarCadence by default).

StarCadence is free and open source. If it helps your observing, you can support it: https://github.com/TechAmatuer/StarCadence#support-starcadence")]

[assembly: InternalsVisibleTo("StarCadence.Nina.Tests")]
