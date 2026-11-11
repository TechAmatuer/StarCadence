// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System.ComponentModel.Composition;
using System.Windows;

namespace StarCadence.Nina {

    /// <summary>N.I.N.A. merges exported resource dictionaries, which is how it finds the instruction's template and icon.</summary>
    [Export(typeof(ResourceDictionary))]
    public partial class Templates : ResourceDictionary {

        public Templates() {
            InitializeComponent();
        }
    }
}
