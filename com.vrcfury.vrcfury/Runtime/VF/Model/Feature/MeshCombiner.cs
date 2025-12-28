using System;
using System.Collections.Generic;
using UnityEngine;

namespace VF.Model.Feature {
    [Serializable]
    internal class MeshCombiner : NewFeatureModel {
        public bool includeChildren = true;
        public List<MeshSource> meshSources = new List<MeshSource>();
        public bool enableUdimMapping = false;
        public int udimUvChannel = 0; // UV channel to apply UDIM offset to (0-7, corresponding to uv, uv2, uv3, uv4, uv5, uv6, uv7, uv8)
        
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

