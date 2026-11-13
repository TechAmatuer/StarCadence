// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using StarCadence.Core.Model;
using StarCadence.Core.Run;

namespace StarCadence.Nina {

    /// <summary>Choices made on the StarCadence Night instruction that change how each step is carried out.</summary>
    public sealed class NinaRunOptions {
        public bool PlateSolve { get; init; } = true;
        public bool ParkAtEnd { get; init; }
        /// <summary>Park instead of only stopping tracking when a wait is this long or longer.</summary>
        public bool ParkWhileWaiting { get; init; }
        public TimeSpan ParkWhenWaitingAtLeast { get; init; } = TimeSpan.FromMinutes(10);
        /// <summary>Set each target's exposure (per filter, once a night) from a test frame instead of from its magnitude.</summary>
        public bool MeasureExposure { get; init; }
        /// <summary>Where the star's peak should land, as a share of the camera's full scale.</summary>
        public double TargetPeakFraction { get; init; } = 0.6;
    }

    /// <summary>
    /// The night runner's view of N.I.N.A. Every action is one of N.I.N.A.'s own sequence instructions (Center or Slew,
    /// Switch Filter, Take Exposure) attached to the StarCadence Night container, so images get the target's name, and
    /// every instruction runs the way N.I.N.A.'s sequencer runs it: the triggers of this container and of every container
    /// above it are checked before and after each step. That is what makes Meridian Flip, Autofocus, Dither After
    /// Exposures and Center After Drift work during a StarCadence night, as they do during a Target Scheduler night.
    /// </summary>
    public class NinaObservatory : IObservatory {
        private readonly ISequenceContainer owner;
        private readonly Action<string, Coordinates> setTarget;
        private readonly IProgress<ApplicationStatus> progress;
        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IImageHistoryVM imageHistoryVM;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IDomeMediator domeMediator;
        private readonly IDomeFollower domeFollower;
        private readonly IPlateSolverFactory plateSolverFactory;
        private readonly IWindowServiceFactory windowServiceFactory;
        private readonly ISafetyMonitorMediator safetyMonitorMediator;
        private readonly NinaRunOptions options;
        private readonly IReadOnlyList<FilterSpec> filters;
        private readonly ISequenceContainer beforeEachTarget;
        private readonly ISequenceContainer afterEachTarget;
        private readonly Action<string> log;
        private readonly Dictionary<(string target, string filter), double> measured = new();
        private ISequenceItem previousStep;

        public NinaObservatory(
            ISequenceContainer owner, Action<string, Coordinates> setTarget, IProgress<ApplicationStatus> progress,
            IProfileService profileService, ITelescopeMediator telescopeMediator, IGuiderMediator guiderMediator,
            ICameraMediator cameraMediator, IImagingMediator imagingMediator, IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM, IFilterWheelMediator filterWheelMediator, IDomeMediator domeMediator,
            IDomeFollower domeFollower, IPlateSolverFactory plateSolverFactory, IWindowServiceFactory windowServiceFactory,
            ISafetyMonitorMediator safetyMonitorMediator, NinaRunOptions options, IReadOnlyList<FilterSpec> filters,
            ISequenceContainer beforeEachTarget, ISequenceContainer afterEachTarget, Action<string> log) {
            this.owner = owner; this.setTarget = setTarget; this.progress = progress;
            this.profileService = profileService; this.telescopeMediator = telescopeMediator; this.guiderMediator = guiderMediator;
            this.cameraMediator = cameraMediator; this.imagingMediator = imagingMediator; this.imageSaveMediator = imageSaveMediator;
            this.imageHistoryVM = imageHistoryVM; this.filterWheelMediator = filterWheelMediator; this.domeMediator = domeMediator;
            this.domeFollower = domeFollower; this.plateSolverFactory = plateSolverFactory; this.windowServiceFactory = windowServiceFactory;
            this.safetyMonitorMediator = safetyMonitorMediator; this.options = options ?? new NinaRunOptions();
            this.filters = filters ?? Array.Empty<FilterSpec>();
            this.beforeEachTarget = beforeEachTarget; this.afterEachTarget = afterEachTarget; this.log = log ?? (_ => { });
        }

        // ---- The steps ----

        public async Task SlewAsync(double raDeg, double decDeg, string targetName, CancellationToken ct) {
            var coords = new Coordinates(Angle.ByDegree(raDeg), Angle.ByDegree(decDeg), Epoch.J2000);
            setTarget(targetName, coords);
            var info = telescopeMediator.GetInfo();
            if (info.Connected && info.AtPark) {
                log("unparking the mount");
                await telescopeMediator.UnparkTelescope(progress, ct);
            } else if (info.Connected && !info.TrackingEnabled) {
                telescopeMediator.SetTrackingMode(TrackingMode.Sidereal);   // stopped while waiting
            }
            var item = CreateSlew(coords, targetName, options.PlateSolve);
            try {
                await RunStep(item, options.PlateSolve ? $"centre on {targetName}" : $"slew to {targetName}", ct);
            } catch (InvalidOperationException e) when (options.PlateSolve) {
                throw new InvalidOperationException(e.Message + ". If plate solving is not set up (Options > Plate Solving), untick \"Plate-solve centring\" in StarCadence Night.", e);
            }
        }

        public async Task SetFilterAsync(string filterName, CancellationToken ct) {
            var filter = LookupFilter(filterName);
            if (filter == null) throw new InvalidOperationException($"Filter '{filterName}' is not in this N.I.N.A. profile's filter list (Options > Equipment > Filter Wheel). Name it the same in StarCadence and N.I.N.A.");
            if (!filterWheelMediator.GetInfo().Connected) {
                if (profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Count > 1) Logger.Warning("StarCadence: no filter wheel connected; exposures use whatever filter is in place");
                return;
            }
            await RunStep(CreateFilterSwitch(filter), $"switch to filter {filterName}", ct);
        }

        public FilterInfo LookupFilter(string name) {
            var list = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            return list.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(f => f.Name != null && (f.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase)));
        }

        public async Task ExposeAsync(ExposureRequest r, CancellationToken ct) {
            var seconds = r.Seconds;
            if (options.MeasureExposure) {
                var key = (r.Target.ToUpperInvariant(), r.Filter.ToUpperInvariant());
                if (!measured.TryGetValue(key, out seconds)) {
                    seconds = await MeasureExposureAsync(r, ct) ?? r.Seconds;
                    measured[key] = seconds;
                }
            }
            await RunStep(CreateExposure(r, seconds), $"{r.Target} {r.Filter} {seconds:0.#} s", ct);
        }

        public Task<bool> IsSafeAsync(CancellationToken ct) {
            var info = safetyMonitorMediator.GetInfo();
            return Task.FromResult(!(info.Connected && !info.IsSafe));
        }

        public Task TargetReadyAsync(string target, CancellationToken ct) => RunSlot(beforeEachTarget, "Before each target", ct);

        public Task TargetDoneAsync(string target, bool completed, CancellationToken ct) => RunSlot(afterEachTarget, "After each target", ct);

        /// <summary>
        /// Nothing to do for a while: stop guiding and tracking so the mount does not track into the meridian or a limit
        /// (Target Scheduler does the same), or park when the person asked for it and the wait is long.
        /// </summary>
        public async Task WaitingAsync(TimeSpan duration, CancellationToken ct) {
            var info = telescopeMediator.GetInfo();
            if (!info.Connected || info.AtPark) return;
            if (guiderMediator.GetInfo().Connected) {
                try { await guiderMediator.StopGuiding(ct); } catch (Exception e) when (e is not OperationCanceledException) { log("could not stop guiding: " + e.Message); }
            }
            if (options.ParkWhileWaiting && duration >= options.ParkWhenWaitingAtLeast) {
                log($"parking while waiting ({duration.TotalMinutes:0} min)");
                await telescopeMediator.ParkTelescope(progress, ct);
            } else if (info.TrackingEnabled) {
                log($"tracking off while waiting ({duration.TotalMinutes:0} min)");
                telescopeMediator.SetTrackingMode(TrackingMode.Stopped);
            }
        }

        public async Task FinishAsync(CancellationToken ct) {
            // The last step's after-triggers, as N.I.N.A. runs them when a container ends.
            if (previousStep != null && !ct.IsCancellationRequested) {
                try { await RunTriggers(previousStep, null, after: true, ct); } catch (Exception e) when (e is not OperationCanceledException) { log("end-of-night triggers: " + e.Message); }
            }
            Release(previousStep);
            previousStep = null;
            var info = telescopeMediator.GetInfo();
            if (options.ParkAtEnd && info.Connected && !info.AtPark) {
                log("parking the mount");
                await telescopeMediator.ParkTelescope(progress, ct);
            }
        }

        // ---- Running N.I.N.A. instructions the way its sequencer does ----

        /// <summary>
        /// Run one N.I.N.A. instruction inside the StarCadence Night container. As in N.I.N.A.'s SequentialStrategy, the
        /// previous step's after-triggers run once the next step is known, then the next step's before-triggers, walking
        /// from this container up to the sequence root; a failed instruction becomes an exception here.
        /// </summary>
        protected async Task RunStep(ISequenceItem item, string what, CancellationToken ct, bool ownedByUs = true) {
            if (item is SequenceItem si && ownedByUs) {
                si.Category = "StarCadence";
                si.Attempts = 1;
                si.ErrorBehavior = InstructionErrorBehavior.ContinueOnError;
            }
            item.AttachNewParent(owner);
            if (previousStep != null) await RunTriggers(previousStep, item, after: true, ct);
            await RunTriggers(previousStep, item, after: false, ct);
            var previous = previousStep;
            previousStep = item;
            Release(previous);
            await item.Run(progress, ct);
            ct.ThrowIfCancellationRequested();
            if (item.Status == SequenceEntityStatus.FAILED) {
                var issues = item is NINA.Sequencer.Validations.IValidatable v && v.Issues != null && v.Issues.Count > 0 ? ": " + string.Join("; ", v.Issues) : "";
                throw new InvalidOperationException($"N.I.N.A. could not {what}{issues}");
            }
        }

        private async Task RunTriggers(ISequenceItem previous, ISequenceItem next, bool after, CancellationToken ct) {
            for (ISequenceContainer c = owner; c != null; c = c.Parent) {
                if (c is ITriggerable t) {
                    if (after) await t.RunTriggersAfter(previous, next, progress, ct);
                    else await t.RunTriggers(previous, next, progress, ct);
                }
            }
        }

        // Instructions made for one step are let go once the next step has started; the person's slot containers stay.
        private void Release(ISequenceItem item) {
            if (item == null || ReferenceEquals(item, beforeEachTarget) || ReferenceEquals(item, afterEachTarget)) return;
            item.AttachNewParent(null);
        }

        private async Task RunSlot(ISequenceContainer slot, string name, CancellationToken ct) {
            if (slot == null || slot.GetItemsSnapshot().Count == 0) return;
            log($"running the \"{name}\" instructions");
            slot.ResetAll();
            try {
                await RunStep(slot, $"run the \"{name}\" instructions", ct, ownedByUs: false);
            } finally {
                slot.ResetAll();
            }
        }

        // ---- The instructions themselves (tests replace these with recording stand-ins) ----

        protected virtual ISequenceItem CreateSlew(Coordinates coords, string targetName, bool plateSolve) {
            if (plateSolve) {
                return new Center(profileService, telescopeMediator, imagingMediator, filterWheelMediator, guiderMediator, domeMediator, domeFollower, plateSolverFactory, windowServiceFactory) {
                    Coordinates = new InputCoordinates(coords)
                };
            }
            return new SlewScopeToRaDec(telescopeMediator, guiderMediator) { Coordinates = new InputCoordinates(coords) };
        }

        protected virtual ISequenceItem CreateFilterSwitch(FilterInfo filter) => new SwitchFilter(profileService, filterWheelMediator) { Filter = filter };

        protected virtual ISequenceItem CreateExposure(ExposureRequest r, double seconds) => new TakeExposure(profileService, cameraMediator, imagingMediator, imageSaveMediator, imageHistoryVM) {
            ExposureTime = seconds,
            ExposureCount = r.Index,
            Gain = -1,
            Offset = -1,
            Binning = new BinningMode(1, 1),
            ImageType = CaptureSequence.ImageTypes.LIGHT,
        };

        // ---- Exposure from a test frame (what the ExoPlanets plug-in's Calculate Exposure Time does) ----

        /// <summary>A test frame of the given length, not saved: its pixels, width and height.</summary>
        protected virtual async Task<(ushort[] pixels, int width, int height)> CaptureTestFrame(double seconds, CancellationToken ct) {
            var capture = new CaptureSequence {
                ExposureTime = seconds, Gain = -1, Offset = -1, Binning = new BinningMode(1, 1),
                ImageType = CaptureSequence.ImageTypes.LIGHT,
            };
            var exposure = await imagingMediator.CaptureImage(capture, ct, progress);
            var image = await exposure.ToImageData(progress, ct);
            return (image.Data.FlatArray, image.Properties.Width, image.Properties.Height);
        }

        protected virtual int CameraBitDepth => cameraMediator.GetInfo().BitDepth;

        /// <summary>
        /// Take test frames on the (just centred) target and return the exposure that puts its peak at the chosen share of
        /// full scale, or null to keep the planned exposure. Shorter tests when the star saturates, longer ones when it is
        /// too faint to measure; at most four frames.
        /// </summary>
        private async Task<double?> MeasureExposureAsync(ExposureRequest r, CancellationToken ct) {
            var spec = filters.FirstOrDefault(f => string.Equals(f.Name, r.Filter, StringComparison.OrdinalIgnoreCase));
            double min = spec?.MinExposure ?? 1, max = spec?.MaxExposure ?? 600;
            var fullScale = ExposureMeter.FullScale(CameraBitDepth);
            var p = profileService.ActiveProfile;
            var scale = ExposureMeter.ArcsecPerPixel(p.CameraSettings.PixelSize, p.TelescopeSettings.FocalLength);
            // How far from the centre the star can be: the centring tolerance when plate solving, a few arcminutes after a plain slew.
            var offArcmin = options.PlateSolve ? Math.Max(0.5, p.PlateSolveSettings.Threshold) : 3.0;
            var radius = double.IsNaN(scale) ? 60 : Math.Clamp(offArcmin * 60 * 1.5 / scale, 15, 400);
            var test = Math.Clamp(r.Seconds / 2, min, max);
            for (var attempt = 1; attempt <= 4; attempt++) {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ApplicationStatus { Source = "StarCadence", Status = $"{r.Target} {r.Filter}: measuring the exposure ({test:0.#} s test)" });
                var (pixels, w, h) = await CaptureTestFrame(test, ct);
                var m = ExposureMeter.Measure(pixels, w, h, w / 2.0, h / 2.0, radius, fullScale);
                var next = ExposureMeter.NextTest(test, m, min, max);
                log($"{r.Target} {r.Filter}: test {test:0.#} s, peak {m.PeakAdu:0} ADU above sky {m.Background:0} (noise {m.Noise:0}){(m.Saturated ? ", saturated" : "")}");
                if (next is null) {
                    if (m.Saturated || !m.Clear) { log($"{r.Target} {r.Filter}: could not measure; keeping the planned {r.Seconds:0.#} s"); return null; }
                    var s = ExposureMeter.Suggest(test, m, fullScale, options.TargetPeakFraction, min, max);
                    log($"{r.Target} {r.Filter}: measured exposure {s:0.#} s (planned {r.Seconds:0.#} s) for a peak at {options.TargetPeakFraction:P0} of {fullScale:0}");
                    return s;
                }
                test = next.Value;
            }
            log($"{r.Target} {r.Filter}: no clear measurement after 4 test frames; keeping the planned {r.Seconds:0.#} s");
            return null;
        }
    }
}
