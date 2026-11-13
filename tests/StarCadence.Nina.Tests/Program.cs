// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.ComponentModel.Composition.Hosting;
using System.Reflection;
using StarCadence.Core.Model;
using StarCadence.Core.Run;

// What N.I.N.A. needs from a plug-in assembly, checked here on the Windows build machine:
// the manifest attributes, the MEF exports (plug-in manifest, sequence item/container, resource dictionary),
// the constructor N.I.N.A. will call, and that the container's night runner wiring compiles against the core.
var failures = new List<string>();
void Check(bool ok, string what) { Console.WriteLine((ok ? "ok    " : "FAIL  ") + what); if (!ok) failures.Add(what); }

var asm = typeof(StarCadence.Nina.StarCadencePlugin).Assembly;
Check(asm.GetCustomAttribute<System.Runtime.InteropServices.GuidAttribute>()?.Value.Length == 36, "plug-in Guid");
Check(asm.GetCustomAttribute<AssemblyTitleAttribute>()?.Title == "StarCadence", "AssemblyTitle = StarCadence");
var meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
foreach (var key in new[] { "MinimumApplicationVersion", "License", "LicenseURL", "Repository", "Homepage", "Tags", "LongDescription" })
    Check(meta.ContainsKey(key) && !string.IsNullOrWhiteSpace(meta[key]), $"AssemblyMetadata {key}");
Check(Version.TryParse(meta.GetValueOrDefault("MinimumApplicationVersion"), out var minV) && minV >= new Version(3, 0), "minimum N.I.N.A. version 3.x");

var catalog = new AssemblyCatalog(asm);
var exports = catalog.Parts.AsEnumerable().SelectMany(p => p.ExportDefinitions.Select(e => (part: p.ToString(), contract: e.ContractName, meta: e.Metadata))).ToList();
foreach (var e in exports) Console.WriteLine($"      export {e.contract} <- {e.part}");
Check(exports.Any(e => e.contract.EndsWith("IPluginManifest")), "exports IPluginManifest (the plug-in)");
Check(exports.Any(e => e.contract.EndsWith("ISequenceItem") && e.part.Contains("StarCadenceNightContainer")), "exports ISequenceItem (StarCadence Night)");
Check(exports.Any(e => e.contract.EndsWith("ISequenceContainer") && e.part.Contains("StarCadenceNightContainer")), "exports ISequenceContainer");
Check(exports.Any(e => e.contract.EndsWith("ResourceDictionary")), "exports a ResourceDictionary (template + icon)");
var item = exports.First(e => e.contract.EndsWith("ISequenceItem"));
Check(item.meta.TryGetValue("Name", out var n) && (string?)n == "StarCadence Night", "ExportMetadata Name");
Check(item.meta.TryGetValue("Icon", out var ic) && (string?)ic == "StarCadence.NightSVG", "ExportMetadata Icon");
Check(item.meta.ContainsKey("Description") && item.meta.ContainsKey("Category"), "ExportMetadata Description and Category");

var containerType = typeof(StarCadence.Nina.StarCadenceNightContainer);
var ctor = containerType.GetConstructors().Single();
Check(ctor.GetCustomAttribute<System.ComponentModel.Composition.ImportingConstructorAttribute>() is not null, "container has an [ImportingConstructor]");
Check(ctor.GetParameters().All(p => p.ParameterType.IsInterface), $"constructor takes only N.I.N.A. services ({ctor.GetParameters().Length})");
Check(typeof(NINA.Sequencer.Container.IDeepSkyObjectContainer).IsAssignableFrom(containerType), "is an IDeepSkyObjectContainer (images get the target name)");
Check(typeof(NINA.Sequencer.Container.SequentialContainer).IsAssignableFrom(containerType), "is a SequentialContainer");
foreach (var prop in new[] { "DataFolder", "PlateSolve", "ParkAtEnd", "ParkWhileWaiting", "MeasureExposure", "TargetPeakPercent", "BeforeEachTarget", "AfterEachTarget" })
    Check(containerType.GetProperty(prop)?.GetCustomAttribute<Newtonsoft.Json.JsonPropertyAttribute>() is not null, $"{prop} is saved with the sequence");

// The template resource dictionary parses and holds the icon and the data template.
var templates = new StarCadence.Nina.Templates();
Check(templates.Contains("StarCadence.NightSVG") && templates["StarCadence.NightSVG"] is System.Windows.Media.GeometryGroup, "icon geometry in the resource dictionary");
Check(templates.Contains(new System.Windows.DataTemplateKey(containerType)), "data template for the container");

// The observatory adapter implements the core's interface, so the tested NightRunner is what drives N.I.N.A.
Check(typeof(IObservatory).IsAssignableFrom(typeof(StarCadence.Nina.NinaObservatory)), "NinaObservatory is an IObservatory");
Check(StarCadence.Nina.StarCadenceNightContainer.DefaultDataFolder().EndsWith("StarCadence"), "default data folder under Documents");

// Run nights inside N.I.N.A.'s own sequencer classes with stand-in equipment.
Console.WriteLine("\nInside N.I.N.A.'s sequencer:");
try { await StarCadence.Nina.Tests.NightInSequencer.RunAll(Check); }
catch (Exception e) { Check(false, "sequencer tests threw: " + e); }

Console.WriteLine(failures.Count == 0 ? "\nall N.I.N.A. plug-in checks passed" : $"\n{failures.Count} check(s) failed");
return failures.Count == 0 ? 0 : 1;
