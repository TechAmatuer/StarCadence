# StarCadence for N.I.N.A.

A N.I.N.A. plug-in that runs a StarCadence night plan: one instruction, **StarCadence Night**, that plans the rest
of the night from your StarCadence settings, observes target after target with N.I.N.A.'s own slew/centre, filter and
exposure steps, records each visit, and plans again until dawn. Transits and eclipse minima keep their time windows;
stars repeat on their cadence.

Needs **N.I.N.A. 3.2 or newer** (Windows).

## Install

1. Set up StarCadence first: run `StarCadence.exe`, fill in Setup (observatory, filters) and Targets, and save. Its
   settings live in `Documents\StarCadence`.
2. Close N.I.N.A.
3. Right-click `StarCadence-nina-plugin.zip` > **Extract All** > **Extract**. You get a folder
   (`StarCadence-nina-plugin`) with `StarCadence.Nina.dll` and `StarCadence.Core.dll` directly inside it.
4. Paste `%LOCALAPPDATA%\NINA\Plugins` into File Explorer's address bar and open the `3.0.0` folder there (create it if
   it isn't there). Copy the whole extracted folder into it. You should end up with
   `...\Plugins\3.0.0\StarCadence-nina-plugin\StarCadence.Nina.dll`: the DLL exactly one folder below `3.0.0`.
5. Start N.I.N.A. Under **Plugins > Installed** you should see StarCadence. (Until it is listed in N.I.N.A.'s plug-in
   repository, this manual copy is the way to install it.)

**Not listed?** Check that the DLL is one folder below `3.0.0` (not two, which happens if you copy the folder that
Extract All made around another folder), and that N.I.N.A. is 3.2 or newer (Help > About). N.I.N.A.'s log
(`%LOCALAPPDATA%\NINA\Logs`) says why a plug-in was not loaded.

## Use

In the Advanced Sequencer, add **StarCadence Night** (category StarCadence) where you would normally put your targets,
after your usual start-of-night steps (unpark, cool camera, autofocus) and inside whatever safety loop you use.
Options on the instruction:

- **Settings folder**: where StarCadence keeps `config.json` (default `Documents\StarCadence`).
- **Plate-solve centring**: use N.I.N.A.'s Center on each target (recommended) instead of a plain slew. Needs plate
  solving set up in N.I.N.A. (Options > Plate Solving).
- **Park at the end**: park the mount when the night is over.

Filter names in StarCadence's Setup must match the names in N.I.N.A.'s filter list (Options > Equipment > Filter
Wheel). Leave the StarCadence Night container empty: it makes its own steps while it runs. While it runs, the PC is
kept from going to sleep (the screen may still turn off). A log of each night is written to `Documents\StarCadence\runs`.

## Status

Built and checked on GitHub's Windows machines against N.I.N.A.'s plug-in library 3.2 (compiles, exports what the
plug-in loader needs, templates parse, and the package is the layout N.I.N.A.'s installer expects). Not yet run inside
a live N.I.N.A. session: please report what you see as an issue at https://github.com/TechAmatuer/StarCadence/issues,
with N.I.N.A.'s log.

StarCadence is free and open source. If it helps your observing, you can support it:
https://github.com/TechAmatuer/StarCadence#support-starcadence
