// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// The shape of this container (a SequentialContainer that is also an IDeepSkyObjectContainer, driven by a planner
// loop) follows Target Scheduler's TargetSchedulerContainer (Tom Palmer, MPL-2.0).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using StarCadence.Core.Model;
using StarCadence.Core.Planning;
using StarCadence.Core.Run;
using StarCadence.Core.Sources;

namespace StarCadence.Nina {

    /// <summary>
    /// One instruction that runs a whole StarCadence night: plan from now, observe the first target with N.I.N.A.'s own
    /// slew/centre, filter and exposure instructions, record the visit, plan again, until dawn.
    /// </summary>
    [ExportMetadata("Name", "StarCadence Night")]
    [ExportMetadata("Description", "Plans and runs tonight's variable stars, transits and eclipse minima from your StarCadence settings, re-planning after every target.")]
    [ExportMetadata("Icon", "StarCadence.NightSVG")]
    [ExportMetadata("Category", "StarCadence")]
    [Export(typeof(ISequenceItem))]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class StarCadenceNightContainer : SequentialContainer, IDeepSkyObjectContainer {
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
        private readonly INighttimeCalculator nighttimeCalculator;
        private readonly ISafetyMonitorMediator safetyMonitorMediator;

        private string dataFolder = DefaultDataFolder();
        private bool plateSolve = true;
        private bool parkAtEnd = false;
        private bool parkWhileWaiting = false;
        private bool measureExposure = false;
        private double targetPeakPercent = 60;
        private string statusText = "Not started";
        private string currentText = "";
        private string nextText = "";
        private string countsText = "";
        private InputTarget target;
        private NighttimeData nighttimeData;

        [ImportingConstructor]
        public StarCadenceNightContainer(
                IProfileService profileService,
                ITelescopeMediator telescopeMediator,
                IGuiderMediator guiderMediator,
                ICameraMediator cameraMediator,
                IImagingMediator imagingMediator,
                IImageSaveMediator imageSaveMediator,
                IImageHistoryVM imageHistoryVM,
                IFilterWheelMediator filterWheelMediator,
                IDomeMediator domeMediator,
                IDomeFollower domeFollower,
                IPlateSolverFactory plateSolverFactory,
                IWindowServiceFactory windowServiceFactory,
                INighttimeCalculator nighttimeCalculator,
                ISafetyMonitorMediator safetyMonitorMediator) : base() {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            this.guiderMediator = guiderMediator;
            this.cameraMediator = cameraMediator;
            this.imagingMediator = imagingMediator;
            this.imageSaveMediator = imageSaveMediator;
            this.imageHistoryVM = imageHistoryVM;
            this.filterWheelMediator = filterWheelMediator;
            this.domeMediator = domeMediator;
            this.domeFollower = domeFollower;
            this.plateSolverFactory = plateSolverFactory;
            this.windowServiceFactory = windowServiceFactory;
            this.nighttimeCalculator = nighttimeCalculator;
            this.safetyMonitorMediator = safetyMonitorMediator;
            Target = EmptyTarget();
            BeforeEachTarget = NewSlot("Before each target");
            AfterEachTarget = NewSlot("After each target");
            Task.Run(() => { try { NighttimeData = nighttimeCalculator?.Calculate(); } catch (Exception) { } });
        }

        // Like Target Scheduler's event containers: ordinary N.I.N.A. instruction sets kept on this instruction (and saved
        // with the sequence), run by StarCadence at the right moment of every target.
        private SequentialContainer NewSlot(string name) {
            var slot = new SequentialContainer { Name = name, Category = "StarCadence" };
            slot.AttachNewParent(this);
            return slot;
        }

        public static string DefaultDataFolder() {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(docs, "StarCadence");
        }

        /// <summary>The StarCadence settings folder (config.json, lists, history).</summary>
        [JsonProperty]
        public string DataFolder { get => dataFolder; set { dataFolder = value; RaisePropertyChanged(); Validate(); } }

        /// <summary>Centre each target with a plate solve (N.I.N.A.'s Center) instead of a plain slew.</summary>
        [JsonProperty]
        public bool PlateSolve { get => plateSolve; set { plateSolve = value; RaisePropertyChanged(); } }

        /// <summary>Park the mount when the night is over.</summary>
        [JsonProperty]
        public bool ParkAtEnd { get => parkAtEnd; set { parkAtEnd = value; RaisePropertyChanged(); } }

        /// <summary>Park during waits of 10 minutes or more (otherwise tracking and guiding just stop while waiting).</summary>
        [JsonProperty]
        public bool ParkWhileWaiting { get => parkWhileWaiting; set { parkWhileWaiting = value; RaisePropertyChanged(); } }

        /// <summary>Set each target's exposure from a test frame (per filter, once a night), like the ExoPlanets plug-in's Calculate Exposure Time.</summary>
        [JsonProperty]
        public bool MeasureExposure { get => measureExposure; set { measureExposure = value; RaisePropertyChanged(); } }

        /// <summary>Where the target star's peak should land, in percent of the camera's full scale (20-90).</summary>
        [JsonProperty]
        public double TargetPeakPercent { get => targetPeakPercent; set { targetPeakPercent = Math.Clamp(value, 20, 90); RaisePropertyChanged(); } }

        /// <summary>Instructions run on each target after the slew or centring and before its exposures (autofocus, for example).</summary>
        [JsonProperty]
        public SequentialContainer BeforeEachTarget { get; set; }

        /// <summary>Instructions run after each target's exposures.</summary>
        [JsonProperty]
        public SequentialContainer AfterEachTarget { get; set; }

        /// <summary>For tests: the clock the night runs on (the computer's clock by default).</summary>
        internal IClock Clock { get; set; } = new SystemClock();

        /// <summary>For tests: makes the observatory adapter (N.I.N.A.'s own instructions by default).</summary>
        internal Func<NinaRunOptions, IReadOnlyList<FilterSpec>, Action<string>, Action<string, Coordinates>, NinaObservatory> ObservatoryFactory { get; set; }

        public override void Initialize() {
            base.Initialize();
            foreach (var slot in Slots()) { slot.AttachNewParent(this); slot.Initialize(); }
        }

        private IEnumerable<SequentialContainer> Slots() {
            if (BeforeEachTarget != null) yield return BeforeEachTarget;
            if (AfterEachTarget != null) yield return AfterEachTarget;
        }

        public string StatusText { get => statusText; private set { statusText = value; RaisePropertyChanged(); } }
        public string CurrentText { get => currentText; private set { currentText = value; RaisePropertyChanged(); } }
        public string NextText { get => nextText; private set { nextText = value; RaisePropertyChanged(); } }
        public string CountsText { get => countsText; private set { countsText = value; RaisePropertyChanged(); } }

        public InputTarget Target { get => target; set { target = value; RaisePropertyChanged(); } }
        public NighttimeData NighttimeData { get => nighttimeData; private set { nighttimeData = value; RaisePropertyChanged(); } }

        private InputTarget EmptyTarget() {
            var p = profileService.ActiveProfile.AstrometrySettings;
            return new InputTarget(Angle.ByDegree(p.Latitude), Angle.ByDegree(p.Longitude), p.Horizon) { TargetName = "StarCadence", Expanded = false };
        }

        private void SetTarget(string name, Coordinates coords) {
            var p = profileService.ActiveProfile.AstrometrySettings;
            var t = new InputTarget(Angle.ByDegree(p.Latitude), Angle.ByDegree(p.Longitude), p.Horizon);
            try {
                var dso = new DeepSkyObject(name, coords, p.Horizon) { Name = name };
                dso.SetDateAndPosition(NighttimeCalculator.GetReferenceDate(DateTime.Now), p.Latitude, p.Longitude);
                t.DeepSkyObject = dso;
            } catch (Exception e) {
                Logger.Warning($"StarCadence: could not prepare the altitude chart for {name}: {e.Message}");
            }
            t.TargetName = name;
            t.InputCoordinates = new InputCoordinates(coords);
            t.Expanded = false;
            Target = t;
        }

        private static void Toast(string message, bool error = false) {
            try { if (error) Notification.ShowError(message); else Notification.ShowInformation(message); } catch (Exception) { /* no N.I.N.A. window (tests) */ }
        }

        public string ConfigPath => Path.Combine(DataFolder ?? "", "config.json");

        public override bool Validate() {
            var issues = new List<string>();
            if (string.IsNullOrWhiteSpace(DataFolder) || !File.Exists(ConfigPath)) {
                issues.Add($"No StarCadence settings at {ConfigPath}. Open StarCadence once and save your observatory and filters.");
            } else {
                try {
                    var cfg = Config.Load(ConfigPath);
                    var profileFilters = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Select(f => f.Name).ToList();
                    var missing = cfg.Filters.Where(f => !profileFilters.Any(n => string.Equals(n, f.Name, StringComparison.OrdinalIgnoreCase))).Select(f => f.Name).ToList();
                    if (missing.Count > 0 && profileFilters.Count > 0) issues.Add($"StarCadence filters not in this profile's filter list: {string.Join(", ", missing)}");
                    var siteLat = profileService.ActiveProfile.AstrometrySettings.Latitude;
                    if (Math.Abs(siteLat - cfg.Site.Latitude) > 0.5) issues.Add($"StarCadence site latitude {cfg.Site.Latitude:0.00} differs from this profile's {siteLat:0.00}");
                } catch (Exception e) {
                    issues.Add("StarCadence settings could not be read: " + e.Message);
                }
            }
            if (!cameraMediator.GetInfo().Connected) issues.Add("Camera not connected");
            if (!telescopeMediator.GetInfo().Connected) issues.Add("Telescope not connected");
            foreach (var slot in Slots()) {
                if (!slot.Validate()) issues.AddRange(slot.GetItemsSnapshot().OfType<NINA.Sequencer.Validations.IValidatable>().SelectMany(v => v.Issues ?? new List<string>()).Select(i => $"{slot.Name}: {i}"));
            }
            Issues = issues;
            RaisePropertyChanged(nameof(Issues));
            return issues.Count == 0;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            Logger.Info($"StarCadence Night: starting with settings in {DataFolder}");
            Config cfg;
            try { cfg = Config.Load(ConfigPath); }
            catch (Exception e) { throw new SequenceEntityFailedException("StarCadence settings could not be read: " + e.Message); }
            var loader = new SourceLoader(cfg, DataFolder, null);
            var historyPath = Path.IsPathRooted(cfg.HistoryFile) ? cfg.HistoryFile : Path.Combine(DataFolder, cfg.HistoryFile);
            var runsDir = Path.Combine(DataFolder, "runs");
            Directory.CreateDirectory(runsDir);
            var logFile = Path.Combine(runsDir, "nina-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            void Log(string line) {
                Logger.Info("StarCadence: " + line);
                try { File.AppendAllText(logFile, line + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            // The PC must not go to sleep in the middle of the night (N.I.N.A. may do this too; asking twice is harmless).
            using var awake = KeepAwake.Start("N.I.N.A. is running a StarCadence night");
            Log(awake.How);
            // No horizon in StarCadence's settings: use the one in this N.I.N.A. profile, as Target Scheduler does.
            if (cfg.Site.Horizon.Count == 0 && profileService.ActiveProfile.AstrometrySettings.Horizon is { } horizon) {
                cfg.Site.Horizon = Enumerable.Range(0, 72).Select(i => new HorizonPoint(i * 5, horizon.GetAltitude(i * 5))).ToList();
                Log($"using this N.I.N.A. profile's custom horizon (highest {cfg.Site.Horizon.Max(h => h.Alt):0} deg)");
            }
            var options = new NinaRunOptions {
                PlateSolve = PlateSolve, ParkAtEnd = ParkAtEnd, ParkWhileWaiting = ParkWhileWaiting,
                MeasureExposure = MeasureExposure, TargetPeakFraction = TargetPeakPercent / 100.0,
            };
            foreach (var slot in Slots()) slot.AttachNewParent(this);   // a sequence loaded from a file does not set this
            var observatory = ObservatoryFactory?.Invoke(options, cfg.Filters, Log, SetTarget)
                ?? new NinaObservatory(this, SetTarget, progress, profileService, telescopeMediator, guiderMediator, cameraMediator,
                    imagingMediator, imageSaveMediator, imageHistoryVM, filterWheelMediator, domeMediator, domeFollower, plateSolverFactory,
                    windowServiceFactory, safetyMonitorMediator, options, cfg.Filters, BeforeEachTarget, AfterEachTarget, Log);
            var runner = new NightRunner(cfg, observatory, Clock, now => loader.LoadAll(now).SelectMany(r => r.Targets), historyPath, Log,
                new RunnerOptions { WaitForDark = true });
            runner.StatusChanged += st => {
                StatusText = st.Message;
                CurrentText = st.Current == null ? "" : $"{st.Current.Target} ({st.Current.Kind}{(st.Current.Window == null ? "" : ", " + st.Current.Window)})";
                NextText = st.Next == null ? "" : $"Next: {st.Next.Target} at {st.Next.StartUtc.ToLocalTime():HH:mm}";
                CountsText = $"{st.BlocksDone} targets done, {st.ExposuresTaken} exposures" + (st.BlocksFailed > 0 ? $", {st.BlocksFailed} failed" : "");
                progress?.Report(new ApplicationStatus { Source = "StarCadence", Status = st.Message });
            };
            runner.BlockStarted += b => Toast($"StarCadence: {b.Target} ({b.Kind})");
            try {
                var done = await runner.RunAsync(token);
                StatusText = $"Night finished: {done} targets.";
                Target = EmptyTarget();
            } catch (OperationCanceledException) {
                StatusText = "Stopped.";
                Target = EmptyTarget();
                throw;
            } catch (Exception e) {
                StatusText = "Failed: " + e.Message;
                Target = EmptyTarget();
                Toast("StarCadence Night: " + e.Message, error: true);
                throw new SequenceEntityFailedException(e.Message, e);
            }
        }

        public override object Clone() {
            var clone = new StarCadenceNightContainer(profileService, telescopeMediator, guiderMediator, cameraMediator, imagingMediator, imageSaveMediator,
                imageHistoryVM, filterWheelMediator, domeMediator, domeFollower, plateSolverFactory, windowServiceFactory, nighttimeCalculator, safetyMonitorMediator) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
                DataFolder = DataFolder,
                PlateSolve = PlateSolve,
                ParkAtEnd = ParkAtEnd,
                ParkWhileWaiting = ParkWhileWaiting,
                MeasureExposure = MeasureExposure,
                TargetPeakPercent = TargetPeakPercent,
                Items = new ObservableCollection<ISequenceItem>(Items.Select(i => i.Clone() as ISequenceItem)),
                Triggers = new ObservableCollection<ISequenceTrigger>(Triggers.Select(t => t.Clone() as ISequenceTrigger)),
                Conditions = new ObservableCollection<ISequenceCondition>(Conditions.Select(c => c.Clone() as ISequenceCondition)),
            };
            foreach (var item in clone.Items) item.AttachNewParent(clone);
            foreach (var condition in clone.Conditions) condition.AttachNewParent(clone);
            foreach (var trigger in clone.Triggers) trigger.AttachNewParent(clone);
            if (BeforeEachTarget != null) { clone.BeforeEachTarget = (SequentialContainer)BeforeEachTarget.Clone(); clone.BeforeEachTarget.Name = BeforeEachTarget.Name; }
            if (AfterEachTarget != null) { clone.AfterEachTarget = (SequentialContainer)AfterEachTarget.Clone(); clone.AfterEachTarget.Name = AfterEachTarget.Name; }
            foreach (var slot in clone.Slots()) slot.AttachNewParent(clone);
            return clone;
        }

        public override string ToString() => $"Category: {Category}, Item: {nameof(StarCadenceNightContainer)}, DataFolder: {DataFolder}";
    }
}
