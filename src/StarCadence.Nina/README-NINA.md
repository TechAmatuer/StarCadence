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
- **Park while waiting**: park during waits of 10 minutes or more. Otherwise tracking and guiding are simply stopped
  while StarCadence waits (for a transit window, say), so the mount never tracks into the meridian unattended.
- **Measure exposure, peak at ... %**: once a night for each target and filter, take a test frame after centring and
  set the exposure so the target star's peak lands at that share of the camera's range (like the ExoPlanets plug-in's
  Calculate Exposure Time). Off: exposures come from the star's magnitude.

Two drop areas on the instruction take ordinary N.I.N.A. instructions:

- **Before each target**: runs on every target after the slew or centring, before its exposures (autofocus, wait for
  the dome, a short settle).
- **After each target**: runs after the target's exposures.

**Triggers work as usual.** StarCadence runs each step the way N.I.N.A.'s sequencer does, so triggers on the
StarCadence Night instruction or on any container above it (Meridian Flip, Autofocus After..., Dither After Exposures,
Center After Drift) are checked before and after every slew, filter change and exposure, and see the current target.

Filter names in StarCadence's Setup must match the names in N.I.N.A.'s filter list (Options > Equipment > Filter
Wheel). If StarCadence has no horizon of its own, the N.I.N.A. profile's custom horizon is used. While a night runs
the PC is kept from going to sleep (the screen may still turn off). A log of each night is written to
`Documents\StarCadence\runs`.

## Status

Built and checked on GitHub's Windows machines against N.I.N.A.'s plug-in library 3.2: it compiles, exports what the
plug-in loader needs, its templates parse, and the package is the layout N.I.N.A.'s installer expects. Whole nights are
also run inside N.I.N.A.'s own sequencer classes with stand-in equipment, checking that triggers fire with the current
target, the before/after-target steps run at the right moments, waits stop tracking, measured exposures are used, and
stopping the sequence stops the night. It has not yet run in a live N.I.N.A. session with real equipment: please
report what you see as an issue at https://github.com/TechAmatuer/StarCadence/issues, with N.I.N.A.'s log.

StarCadence is free and open source. If it helps your observing, you can support it:
https://github.com/TechAmatuer/StarCadence#support-starcadence
