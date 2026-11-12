// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;
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
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using StarCadence.Core.Run;

namespace StarCadence.Nina {

    /// <summary>
    /// The night runner's view of N.I.N.A.: every action is carried out by one of N.I.N.A.'s own sequence instructions
    /// (Center or Slew, Switch Filter, Take Exposure), attached to the StarCadence Night container so images get the
    /// target name, and run the way the sequencer runs them. This is how Target Scheduler drives N.I.N.A. too.
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
        private readonly bool plateSolve;
        private readonly bool parkAtEnd;

        public NinaObservatory(
            ISequenceContainer owner, Action<string, Coordinates> setTarget, IProgress<ApplicationStatus> progress,
            IProfileService profileService, ITelescopeMediator telescopeMediator, IGuiderMediator guiderMediator,
            ICameraMediator cameraMediator, IImagingMediator imagingMediator, IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM, IFilterWheelMediator filterWheelMediator, IDomeMediator domeMediator,
            IDomeFollower domeFollower, IPlateSolverFactory plateSolverFactory, IWindowServiceFactory windowServiceFactory,
            ISafetyMonitorMediator safetyMonitorMediator, bool plateSolve, bool parkAtEnd) {
            this.owner = owner; this.setTarget = setTarget; this.progress = progress;
            this.profileService = profileService; this.telescopeMediator = telescopeMediator; this.guiderMediator = guiderMediator;
            this.cameraMediator = cameraMediator; this.imagingMediator = imagingMediator; this.imageSaveMediator = imageSaveMediator;
            this.imageHistoryVM = imageHistoryVM; this.filterWheelMediator = filterWheelMediator; this.domeMediator = domeMediator;
            this.domeFollower = domeFollower; this.plateSolverFactory = plateSolverFactory; this.windowServiceFactory = windowServiceFactory;
            this.safetyMonitorMediator = safetyMonitorMediator; this.plateSolve = plateSolve; this.parkAtEnd = parkAtEnd;
        }

        public async Task SlewAsync(double raDeg, double decDeg, string targetName, CancellationToken ct) {
            var coords = new Coordinates(Angle.ByDegree(raDeg), Angle.ByDegree(decDeg), Epoch.J2000);
            setTarget(targetName, coords);
            if (telescopeMediator.GetInfo().AtPark) {
                Logger.Info("StarCadence: unparking the mount");
                await telescopeMediator.UnparkTelescope(progress, ct);
            }
            SequenceItem item;
            if (plateSolve) {
                var center = new Center(profileService, telescopeMediator, imagingMediator, filterWheelMediator, guiderMediator, domeMediator, domeFollower, plateSolverFactory, windowServiceFactory) {
                    Coordinates = new InputCoordinates(coords)
                };
                item = center;
            } else {
                var slew = new SlewScopeToRaDec(telescopeMediator, guiderMediator) { Coordinates = new InputCoordinates(coords) };
                item = slew;
            }
            try {
                await RunItem(item, plateSolve ? $"centre on {targetName}" : $"slew to {targetName}", ct);
            } catch (InvalidOperationException e) when (plateSolve) {
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
            var item = new SwitchFilter(profileService, filterWheelMediator) { Filter = filter };
            await RunItem(item, $"switch to filter {filterName}", ct);
        }

        public FilterInfo LookupFilter(string name) {
            var filters = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            return filters.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? filters.FirstOrDefault(f => f.Name != null && (f.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase)));
        }

        public async Task ExposeAsync(ExposureRequest r, CancellationToken ct) {
            var item = new TakeExposure(profileService, cameraMediator, imagingMediator, imageSaveMediator, imageHistoryVM) {
                ExposureTime = r.Seconds,
                ExposureCount = r.Index,
                Gain = -1,
                Offset = -1,
                Binning = new BinningMode(1, 1),
                ImageType = CaptureSequence.ImageTypes.LIGHT,
            };
            await RunItem(item, $"{r.Target} {r.Filter} {r.Seconds:0.#} s", ct);
        }

        public Task<bool> IsSafeAsync(CancellationToken ct) {
            var info = safetyMonitorMediator.GetInfo();
            return Task.FromResult(!(info.Connected && !info.IsSafe));
        }

        public async Task FinishAsync(CancellationToken ct) {
            if (parkAtEnd && telescopeMediator.GetInfo().Connected && !telescopeMediator.GetInfo().AtPark) {
                Logger.Info("StarCadence: parking the mount");
                await telescopeMediator.ParkTelescope(progress, ct);
            }
        }

        /// <summary>Run one of N.I.N.A.'s instructions as the sequencer would; a failed instruction becomes an exception here.</summary>
        private async Task RunItem(SequenceItem item, string what, CancellationToken ct) {
            item.Category = "StarCadence";
            item.Attempts = 1;
            item.ErrorBehavior = InstructionErrorBehavior.ContinueOnError;
            item.AttachNewParent(owner);
            try {
                await item.Run(progress, ct);
                ct.ThrowIfCancellationRequested();
                if (item.Status == SequenceEntityStatus.FAILED) {
                    var issues = item is NINA.Sequencer.Validations.IValidatable v && v.Issues != null && v.Issues.Count > 0 ? ": " + string.Join("; ", v.Issues) : "";
                    throw new InvalidOperationException($"N.I.N.A. could not {what}{issues}");
                }
            } finally {
                item.AttachNewParent(null);
            }
        }
    }
}
