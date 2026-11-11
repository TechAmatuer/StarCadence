# StarCadence for N.I.N.A.

A N.I.N.A. plug-in that runs a StarCadence night plan: one instruction, **StarCadence Night**, that plans the rest
of the night from your StarCadence settings, observes target after target with N.I.N.A.'s own slew/centre, filter and
exposure steps, records each visit, and plans again until dawn. Transits and eclipse minima keep their time windows;
stars repeat on their cadence.

## Install

1. Set up StarCadence first: run `StarCadence.exe`, fill in Setup (observatory, filters) and Targets, and save. Its
   settings live in `Documents\StarCadence`.
2. Close N.I.N.A. Unzip `StarCadence-nina-plugin.zip` and copy the `StarCadence` folder into
   `%LOCALAPPDATA%\NINA\Plugins\3.0.0\` (paste that path into File Explorer's address bar; create the folder if it
   isn't there). You should end up with `...\Plugins\3.0.0\StarCadence\StarCadence.Nina.dll`.
3. Start N.I.N.A. In **Plugins** you should see StarCadence. (Until it is listed in N.I.N.A.'s plug-in repository,
   this manual copy is the way to install it.)

## Use

In the Advanced Sequencer, add **StarCadence Night** (category StarCadence) where you would normally put your targets,
after your usual start-of-night steps (unpark, cool camera, autofocus) and inside whatever safety loop you use.
Options on the instruction:

- **Settings folder**: where StarCadence keeps `config.json` (default `Documents\StarCadence`).
- **Plate-solve centring**: use N.I.N.A.'s Center on each target (recommended) instead of a plain slew.
- **Park at the end**: park the mount when the night is over.

Filter names in StarCadence's Setup must match the names in N.I.N.A.'s filter list (Options > Equipment > Filter
Wheel). Leave the StarCadence Night container empty: it makes its own steps while it runs.

## Status

Built and checked on GitHub's Windows machines against N.I.N.A.'s plug-in library 3.2 (compiles, exports what the
plug-in loader needs, templates parse). Not yet run inside a live N.I.N.A. session: please report what you see as an
issue at https://github.com/TechAmatuer/StarCadence/issues, with N.I.N.A.'s log.
