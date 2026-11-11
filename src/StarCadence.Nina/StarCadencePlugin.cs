// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace StarCadence.Nina {

    /// <summary>The plug-in itself. Its settings live in StarCadence's own folder, so there is nothing to configure here.</summary>
    [Export(typeof(IPluginManifest))]
    public class StarCadencePlugin : PluginBase {

        [ImportingConstructor]
        public StarCadencePlugin() {
        }
    }
}
