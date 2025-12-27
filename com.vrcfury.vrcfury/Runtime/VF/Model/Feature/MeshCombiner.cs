using System;
using System.Collections.Generic;
using UnityEngine;

namespace VF.Model.Feature {
    [Serializable]
    internal class MeshCombiner : NewFeatureModel {
        public bool includeChildren = true;
        public List<MeshSource> meshSources = new List<MeshSource>();
        public bool enableUdimMapping = false;
        
        [Serializable]
        public class MeshSource {
            public GameObject obj;
            public int udimTile = 0; // UDIM tile index (0-9 for UDIM 1001-1010)
        }
        
        public override int GetLatestVersion() {
            return 0;
        }
    }
}

