// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using Moq;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using StarCadence.Core.Model;
using StarCadence.Core.Planning;
using StarCadence.Core.Run;

namespace StarCadence.Nina.Tests;

/// <summary>
/// Runs StarCadence Night inside N.I.N.A.'s own sequencer classes (SequentialContainer, SequenceItem, SequenceTrigger,
/// the trigger chain, ItemUtility), with stand-in equipment and stand-in steps that record what they are asked to do.
/// Checks what Target Scheduler users rely on: triggers placed above or on the instruction fire between steps and see the
/// current target, the before/after-target instructions run at the right moments, waits stop tracking, and stopping works.
/// </summary>
static class NightInSequencer {
    sealed record Rig(StarCadenceNightContainer Night, Mock<ITelescopeMediator> Telescope, Mock<IGuiderMediator> Guider, List<string> Steps,
                      List<(string what, Coordinates? at, Coordinates? expected)> TriggerLog, string DataFolder);

    public static async Task RunAll(Action<bool, string> check) {
        await TriggersAndSlots(check);
        await MeasuredExposure(check);
        await WaitsStopTrackingOrPark(check);
        await StopsWhenCancelled(check);
        ClonesTheSlots(check);
    }

    // ---- 1. A short night: steps in order, triggers fire with the target as context, slots run ----
    static async Task TriggersAndSlots(Action<bool, string> check) {
        var rig = Make();
        var parent = new SequentialContainer { Name = "Target area" };
        var above = new RecordingTrigger("above", rig.TriggerLog);           // like a Meridian Flip trigger in the target area
        var onIt = new RecordingTrigger("on StarCadence Night", rig.TriggerLog);
        parent.Add(above);
        rig.Night.Add(onIt);
        rig.Night.BeforeEachTarget.Add(new RecordingStep("before-target step", rig.Steps));
        rig.Night.AfterEachTarget.Add(new RecordingStep("after-target step", rig.Steps));
        parent.Add(rig.Night);

        await parent.Run(new Progress<ApplicationStatus>(), CancellationToken.None);

        var s = rig.Steps;
        Console.WriteLine("      steps: " + string.Join(" | ", s.Take(14)) + (s.Count > 14 ? $" ... ({s.Count})" : ""));
        var firstSlew = s.FindIndex(x => x.StartsWith("slew "));
        check(firstSlew >= 0, "a StarCadence night ran inside N.I.N.A.'s sequencer");
        var target = firstSlew >= 0 ? s[firstSlew][5..] : "?";
        var before = s.IndexOf("before-target step");
        var firstExposure = s.FindIndex(x => x.StartsWith($"expose {target} "));
        var lastExposure = s.FindLastIndex(x => x.StartsWith($"expose {target} "));
        var after = s.IndexOf("after-target step");
        check(firstSlew < before && before < firstExposure, "\"Before each target\" ran after the slew and before the exposures");
        check(after > lastExposure && lastExposure >= firstExposure, "\"After each target\" ran after the target's exposures");
        check(s.Count(x => x == "before-target step") == s.Count(x => x.StartsWith("slew ")), "\"Before each target\" ran once per target");

        var aboveBeforeExposure = rig.TriggerLog.Count(t => t.what.StartsWith("above: before expose"));
        check(aboveBeforeExposure >= 1, $"a trigger in the container above was checked before exposures ({aboveBeforeExposure}x)");
        check(rig.TriggerLog.Any(t => t.what.StartsWith("on StarCadence Night: before expose")), "a trigger on StarCadence Night itself was checked too");
        check(rig.TriggerLog.Any(t => t.what.StartsWith("above: after ") && t.what.Contains(" next=expose")),
              "after-triggers run between steps and see the next exposure (what Dither After Exposures needs)");
        var ran = rig.TriggerLog.Where(t => t.what.StartsWith("above: ran")).ToList();
        check(ran.Count >= 1, $"the trigger's action ran during the night, where Meridian Flip would flip ({ran.Count}x)");
        check(ran.Count >= 1 && ran.All(r => r.at is not null && r.expected is not null
                  && Math.Abs(r.at.RADegrees - r.expected.RADegrees) < 1e-6 && Math.Abs(r.at.Dec - r.expected.Dec) < 1e-6),
              "the trigger saw the current target's coordinates (Meridian Flip and Center After Drift re-centre on them)");
        check(rig.Night.Status != SequenceEntityStatus.FAILED, $"the night ended without failing ({rig.Night.StatusText})");
        Directory.Delete(rig.DataFolder, true);
    }

    // ---- 2. Exposure from a test frame ----
    static async Task MeasuredExposure(Action<bool, string> check) {
        var rig = Make(measure: true);
        var parent = new SequentialContainer();
        parent.Add(rig.Night);
        await parent.Run(new Progress<ApplicationStatus>(), CancellationToken.None);
        var tests = rig.Steps.Where(x => x.StartsWith("test frame")).ToList();
        check(tests.Count >= 1, $"test frames were taken ({tests.Count})");
        // The stand-in camera gives 1500 ADU of peak per second over a sky of 1000: for 60% of 65535 that is (39321-1000)/1500 = 25.5 s -> 25 s.
        var exposures = rig.Steps.Where(x => x.StartsWith("expose ")).Select(x => double.Parse(x.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        check(exposures.Count > 0 && exposures.All(e => Math.Abs(e - 25) < 0.01), $"exposures use the measured 25 s ({string.Join(", ", exposures.Distinct())})");
        var perTargetFilter = rig.Steps.Count(x => x.StartsWith("test frame"));
        var pairs = rig.Steps.Where(x => x.StartsWith("expose ")).Select(x => string.Join(' ', x.Split(' ')[1..^1])).Distinct().Count();
        check(perTargetFilter <= pairs * 2, $"measured once per target and filter, not before every frame ({perTargetFilter} tests, {pairs} target/filter pairs)");
        Directory.Delete(rig.DataFolder, true);
    }

    // ---- 3. Waiting stops guiding and tracking, or parks when asked ----
    static async Task WaitsStopTrackingOrPark(Action<bool, string> check) {
        var rig = Make();
        var obs = (RecordingObservatory)rig.Night.ObservatoryFactory!(new NinaRunOptions(), Array.Empty<FilterSpec>(), _ => { }, (_, _) => { });
        await obs.WaitingAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        rig.Telescope.Verify(t => t.SetTrackingMode(TrackingMode.Stopped), Times.Once(), "tracking stopped while waiting");
        rig.Guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Once(), "guiding stopped while waiting");
        check(true, "waiting stops guiding and tracking (Target Scheduler does the same)");
        var parking = (RecordingObservatory)rig.Night.ObservatoryFactory!(new NinaRunOptions { ParkWhileWaiting = true }, Array.Empty<FilterSpec>(), _ => { }, (_, _) => { });
        await parking.WaitingAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        rig.Telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once());
        await parking.WaitingAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        rig.Telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once());
        check(true, "\"Park while waiting\" parks for long waits only");
        Directory.Delete(rig.DataFolder, true);
    }

    // ---- 4. Stopping the sequence stops the night and parks if asked ----
    static async Task StopsWhenCancelled(Action<bool, string> check) {
        var rig = Make(parkAtEnd: true, start: -3);
        var parent = new SequentialContainer();
        parent.Add(rig.Night);
        using var cts = new CancellationTokenSource();
        RecordingStep.OnExpose = () => { if (rig.Steps.Count(x => x.StartsWith("expose ")) >= 2) cts.Cancel(); };
        try { await parent.Run(new Progress<ApplicationStatus>(), cts.Token); } catch (OperationCanceledException) { }
        RecordingStep.OnExpose = null;
        var exposed = rig.Steps.Count(x => x.StartsWith("expose "));
        check(exposed >= 2 && exposed <= 3, $"stopping the sequence stopped the night ({exposed} exposures)");
        rig.Telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        check(true, "the mount was parked after the stop (Park at the end)");
        Directory.Delete(rig.DataFolder, true);
    }

    // ---- 5. Copying the instruction (templates, drag and drop) keeps the slot instructions ----
    static void ClonesTheSlots(Action<bool, string> check) {
        var rig = Make();
        rig.Night.BeforeEachTarget.Add(new RecordingStep("autofocus stand-in", rig.Steps));
        rig.Night.MeasureExposure = true; rig.Night.TargetPeakPercent = 55; rig.Night.ParkWhileWaiting = true;
        var copy = (StarCadenceNightContainer)rig.Night.Clone();
        check(copy.BeforeEachTarget.Items.Count == 1 && copy.BeforeEachTarget.Name == "Before each target" && copy.AfterEachTarget is not null,
              "a copy of the instruction keeps its \"Before/After each target\" instructions");
        check(copy.BeforeEachTarget.Parent == copy, "the copy's slots belong to the copy");
        check(copy.MeasureExposure && copy.TargetPeakPercent == 55 && copy.ParkWhileWaiting, "a copy keeps the new options");
        foreach (var p in new[] { "BeforeEachTarget", "AfterEachTarget", "ParkWhileWaiting", "MeasureExposure", "TargetPeakPercent" })
            check(typeof(StarCadenceNightContainer).GetProperty(p)?.GetCustomAttributes(typeof(Newtonsoft.Json.JsonPropertyAttribute), false).Length == 1, $"{p} is saved with the sequence");
        Directory.Delete(rig.DataFolder, true);
    }

    // ---- The rig: stand-in N.I.N.A. services, StarCadence settings, a fast clock near the end of a night ----
    static Rig Make(bool measure = false, bool parkAtEnd = false, double start = -1.6) {
        var data = Path.Combine(Path.GetTempPath(), "sc-nina-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        foreach (var f in new[] { "config.json", "sample-targets.csv", "sample-exoclock.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "examples", f), Path.Combine(data, f));

        var profile = new Mock<IProfile> { DefaultValue = DefaultValue.Mock };
        profile.SetupGet(p => p.AstrometrySettings.Latitude).Returns(40);
        profile.SetupGet(p => p.AstrometrySettings.Longitude).Returns(-75);
        profile.SetupGet(p => p.AstrometrySettings.Horizon).Returns((CustomHorizon)null!);   // no custom horizon in this profile
        profile.SetupGet(p => p.FilterWheelSettings.FilterWheelFilters).Returns(new ObserveAllCollection<FilterInfo>(
            new[] { "B", "V", "R", "I" }.Select((n, i) => new FilterInfo(n, 0, (short)i))));
        profile.SetupGet(p => p.PlateSolveSettings.Threshold).Returns(1.0);
        profile.SetupGet(p => p.CameraSettings.PixelSize).Returns(3.76);
        profile.SetupGet(p => p.TelescopeSettings.FocalLength).Returns(530);
        var profileService = new Mock<IProfileService>();
        profileService.SetupGet(p => p.ActiveProfile).Returns(profile.Object);

        var telescope = new Mock<ITelescopeMediator>();
        telescope.Setup(t => t.GetInfo()).Returns(new TelescopeInfo { Connected = true, TrackingEnabled = true });
        telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        telescope.Setup(t => t.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var guider = new Mock<IGuiderMediator>();
        guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
        guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var camera = new Mock<ICameraMediator>();
        camera.Setup(c => c.GetInfo()).Returns(new CameraInfo { Connected = true, BitDepth = 16 });
        var wheel = new Mock<IFilterWheelMediator>();
        wheel.Setup(w => w.GetInfo()).Returns(new FilterWheelInfo { Connected = true });
        var safety = new Mock<ISafetyMonitorMediator>();
        safety.Setup(s => s.GetInfo()).Returns(new SafetyMonitorInfo { Connected = false });

        var night = new StarCadenceNightContainer(profileService.Object, telescope.Object, guider.Object, camera.Object,
            Mock.Of<IImagingMediator>(), Mock.Of<IImageSaveMediator>(), Mock.Of<IImageHistoryVM>(), wheel.Object, Mock.Of<IDomeMediator>(),
            Mock.Of<IDomeFollower>(), Mock.Of<IPlateSolverFactory>(), Mock.Of<IWindowServiceFactory>(), Mock.Of<INighttimeCalculator>(), safety.Object) {
            DataFolder = data, PlateSolve = false, ParkAtEnd = parkAtEnd, MeasureExposure = measure,
        };
        // The last hours of the night of 3-4 December 2026 at the example site, 1500 times faster than real time.
        var cfg = Config.Load(Path.Combine(data, "config.json"));
        var n = Night.From(cfg.Site, new DateTime(2026, 12, 4, 3, 0, 0, DateTimeKind.Utc), cfg.Planner.StepMinutes)!;
        var clock = new ScaledClock(n.EndUtc.AddHours(start), 1500);
        night.Clock = clock;
        var steps = new List<string>();
        var triggers = new List<(string, Coordinates?, Coordinates?)>();
        night.ObservatoryFactory = (options, filters, log, setTarget) => new RecordingObservatory(night, setTarget, profileService.Object, telescope.Object, guider.Object,
            camera.Object, wheel.Object, safety.Object, options, filters, log, steps, clock);
        return new Rig(night, telescope, guider, steps, triggers, data);
    }
}

/// <summary>The real adapter with stand-in steps: everything but the equipment is N.I.N.A.'s and StarCadence's own code.</summary>
sealed class RecordingObservatory : NinaObservatory {
    readonly List<string> _steps; readonly IClock _clock;
    public RecordingObservatory(StarCadenceNightContainer night, Action<string, Coordinates> setTarget, IProfileService profile, ITelescopeMediator telescope,
        IGuiderMediator guider, ICameraMediator camera, IFilterWheelMediator wheel, ISafetyMonitorMediator safety, NinaRunOptions options,
        IReadOnlyList<FilterSpec> filters, Action<string> log, List<string> steps, IClock clock)
        : base(night, setTarget, new Progress<ApplicationStatus>(), profile, telescope, guider, camera, Mock.Of<IImagingMediator>(),
               Mock.Of<IImageSaveMediator>(), Mock.Of<IImageHistoryVM>(), wheel, Mock.Of<IDomeMediator>(), Mock.Of<IDomeFollower>(),
               Mock.Of<IPlateSolverFactory>(), Mock.Of<IWindowServiceFactory>(), safety, options, filters, night.BeforeEachTarget, night.AfterEachTarget, log) {
        _steps = steps; _clock = clock;
    }
    protected override ISequenceItem CreateSlew(Coordinates coords, string targetName, bool plateSolve) {
        RecordingStep.LastCoordinates = coords;
        return new RecordingStep($"slew {targetName}", _steps, _clock, 60);
    }
    protected override ISequenceItem CreateFilterSwitch(FilterInfo filter) => new RecordingStep($"filter {filter.Name}", _steps, _clock, 5);
    protected override ISequenceItem CreateExposure(ExposureRequest r, double seconds) =>
        new RecordingStep($"expose {r.Target} {r.Filter} {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}", _steps, _clock, seconds + 2, exposure: true);
    protected override int CameraBitDepth => 16;
    protected override async Task<(ushort[] pixels, int width, int height)> CaptureTestFrame(double seconds, CancellationToken ct) {
        _steps.Add($"test frame {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        await _clock.Delay(TimeSpan.FromSeconds(seconds + 2), ct);
        // A star at the centre with 1500 ADU of peak per second over a sky of 1000.
        const int w = 200, h = 160;
        var px = new ushort[w * h];
        var peak = 1500 * seconds;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                px[y * w + x] = (ushort)Math.Min(65535, 1000 + ((x + y) % 7) + peak * Math.Exp(-((x - 100) * (x - 100) + (y - 80) * (y - 80)) / 8.0));
        return (px, w, h);
    }
}

/// <summary>A N.I.N.A. sequence item that records that it ran and takes simulated time.</summary>
sealed class RecordingStep : SequenceItem {
    public static Coordinates? LastCoordinates;
    public static Action? OnExpose;
    readonly List<string> _steps; readonly IClock? _clock; readonly double _seconds;
    public bool IsExposure { get; }
    public RecordingStep(string name, List<string> steps, IClock? clock = null, double seconds = 0, bool exposure = false) {
        Name = name; _steps = steps; _clock = clock; _seconds = seconds; IsExposure = exposure;
    }
    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
        _steps.Add(Name);
        if (IsExposure) OnExpose?.Invoke();
        if (_clock is not null && _seconds > 0) await _clock.Delay(TimeSpan.FromSeconds(_seconds), token);
    }
    public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(_seconds);
    public override object Clone() => new RecordingStep(Name, _steps, _clock, _seconds, IsExposure);
}

/// <summary>A trigger that records when N.I.N.A.'s trigger chain consults it, and fires before every exposure.</summary>
sealed class RecordingTrigger : SequenceTrigger {
    readonly List<(string what, Coordinates? at, Coordinates? expected)> _log; readonly string _label;
    public RecordingTrigger(string label, List<(string, Coordinates?, Coordinates?)> log) { _label = label; _log = log; Name = "Recording trigger " + label; }
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
        _log.Add(($"{_label}: before {nextItem?.Name} prev={previousItem?.Name}", null, null));
        return nextItem is RecordingStep { IsExposure: true };
    }
    public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) {
        _log.Add(($"{_label}: after {previousItem?.Name} next={nextItem?.Name}", null, null));
        return false;
    }
    public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
        var at = ItemUtility.RetrieveContextCoordinates(context)?.Coordinates;
        _log.Add(($"{_label}: ran", at, RecordingStep.LastCoordinates));
        return Task.CompletedTask;
    }
    public override object Clone() => new RecordingTrigger(_label, _log);
}
