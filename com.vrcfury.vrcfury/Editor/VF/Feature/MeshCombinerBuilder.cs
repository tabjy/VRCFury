using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VF.Builder;
using VF.Feature.Base;
using VF.Injector;
using VF.Inspector;
using VF.Model.Feature;
using VF.Service;
using VF.Utils;

namespace VF.Feature {
    [FeatureTitle("Mesh Combiner")]
    internal class MeshCombinerBuilder : FeatureBuilder<MeshCombiner> {
        [VFAutowired] private readonly VFGameObject avatarObject;
        [VFAutowired] private readonly GlobalsService globals;
        
        [FeatureBuilderAction(FeatureOrder.Default)]
        public void Apply() {
            var componentObject = featureBaseObject;
            var renderers = new List<Renderer>();
            
            if (model.meshSources.Count > 0) {
                // Use explicitly specified mesh sources
                foreach (var source in model.meshSources) {
                    if (source.obj == null) continue;
                    var obj = source.obj;
                    var renderer = obj.GetComponent<Renderer>();
                    if (renderer == null) {
                        Debug.LogWarning($"MeshCombiner: Object {obj.name} does not have a Renderer component");
                        continue;
                    }
                    renderers.Add(renderer);
                }
            } else if (model.includeChildren) {
                // Collect all renderers from children
                renderers.AddRange(componentObject.GetComponentsInSelfAndChildren<Renderer>());
            } else {
                // Only direct children
                foreach (var child in componentObject.Children()) {
                    var renderer = child.GetComponent<Renderer>();
                    if (renderer != null) {
                        renderers.Add(renderer);
                    }
                }
            }
            
            if (renderers.Count == 0) {
                Debug.LogWarning($"MeshCombiner: No renderers found on {componentObject.GetPath(avatarObject)}");
                return;
            }
            
            // Filter to only MeshRenderers and SkinnedMeshRenderers with valid meshes
            var validRenderers = renderers
                .Where(r => {
                    if (r is SkinnedMeshRenderer skin) {
                        return skin.sharedMesh != null;
                    }
                    if (r is MeshRenderer) {
                        var filter = r.owner().GetComponent<MeshFilter>();
                        return filter != null && filter.sharedMesh != null;
                    }
                    return false;
                })
                .ToList();
            
            if (validRenderers.Count == 0) {
                Debug.LogWarning($"MeshCombiner: No valid meshes found on {componentObject.GetPath(avatarObject)}");
                return;
            }
            
            if (validRenderers.Count == 1) {
                Debug.Log($"MeshCombiner: Only one renderer found, skipping combine on {componentObject.GetPath(avatarObject)}");
                return;
            }
            
            // Combine meshes
            CombineMeshes(componentObject, validRenderers);
        }
        
        private void CombineMeshes(VFGameObject targetObject, List<Renderer> renderers) {
            var combinedMesh = new Mesh();
            combinedMesh.name = $"Combined_{targetObject.name}";
            
            // Collect all unique materials from all renderers
            var materialToIndex = new Dictionary<Material, int>(); // material -> index in combined materials array
            var uniqueMaterials = new List<Material>();
            
            var blendshapeData = new Dictionary<string, List<(int vertexOffset, int vertexCount, Mesh sourceMesh, int blendshapeIndex, Matrix4x4 transform)>>();
            
            // Collect all vertex data manually to combine into multiple submeshes (one per unique material)
            var allVertices = new List<Vector3>();
            var allNormals = new List<Vector3>();
            var allTangents = new List<Vector4>();
            var allUvs = new List<Vector2>();
            var trianglesByMaterial = new Dictionary<int, List<int>>(); // material index -> triangles list
            var allColors = new List<Color32>();
            var allBoneWeights = new List<BoneWeight>();
            var allBones = new List<Transform>();
            var allBindPoses = new List<Matrix4x4>();
            var boneIndexMap = new Dictionary<SkinnedMeshRenderer, Dictionary<int, int>>(); // Maps each skin's bone index to combined bone index
            
            // First pass: collect all unique bones from all meshes
            var boneToIndex = new Dictionary<Transform, int>(); // bone -> index in combined bones array
            var boneToBindPose = new Dictionary<Transform, Matrix4x4>(); // bone -> bindpose
            
            foreach (var renderer in renderers) {
                if (renderer is SkinnedMeshRenderer skin && skin.bones != null && skin.sharedMesh != null) {
                    var mesh = skin.sharedMesh;
                    for (int i = 0; i < skin.bones.Length && i < mesh.bindposes.Length; i++) {
                        var bone = skin.bones[i];
                        if (bone != null) {
                            if (!boneToIndex.ContainsKey(bone)) {
                                boneToIndex[bone] = allBones.Count;
                                allBones.Add(bone);
                                allBindPoses.Add(mesh.bindposes[i]);
                                boneToBindPose[bone] = mesh.bindposes[i];
                            }
                        }
                    }
                }
            }
            
            // Second pass: create bone index mapping for each skin
            foreach (var renderer in renderers) {
                if (renderer is SkinnedMeshRenderer skin && skin.bones != null) {
                    var indexMap = new Dictionary<int, int>();
                    for (int i = 0; i < skin.bones.Length; i++) {
                        var bone = skin.bones[i];
                        if (bone != null && boneToIndex.ContainsKey(bone)) {
                            indexMap[i] = boneToIndex[bone];
                        }
                    }
                    boneIndexMap[skin] = indexMap;
                }
            }
            
            // Third pass: collect all unique materials from all renderers
            foreach (var renderer in renderers) {
                if (renderer.sharedMaterials != null) {
                    foreach (var material in renderer.sharedMaterials) {
                        if (material != null && !materialToIndex.ContainsKey(material)) {
                            materialToIndex[material] = uniqueMaterials.Count;
                            uniqueMaterials.Add(material);
                        }
                    }
                }
            }
            
            // Fourth pass: collect mesh data - for skinned meshes, keep vertices in original space
            var meshData = new List<(Mesh originalMesh, int vertexCount, Renderer renderer, bool isSkinned, SkinnedMeshRenderer skin)>();
            
            foreach (var renderer in renderers) {
                var mesh = renderer.GetMesh();
                if (mesh == null) continue;
                
                var isSkinned = renderer is SkinnedMeshRenderer skin && mesh.boneWeights.Length > 0;
                var skinRef = renderer as SkinnedMeshRenderer;
                
                meshData.Add((mesh, mesh.vertexCount, renderer, isSkinned, skinRef));
            }
            
            // Fifth pass: collect blendshape data from original meshes
            var vertexOffset = 0;
            foreach (var (originalMesh, vertexCount, renderer, isSkinned, skin) in meshData) {
                // For blendshapes, we don't need to transform (they're deltas)
                var identityMatrix = Matrix4x4.identity;
                for (int i = 0; i < originalMesh.blendShapeCount; i++) {
                    var blendshapeName = originalMesh.GetBlendShapeName(i);
                    if (!blendshapeData.ContainsKey(blendshapeName)) {
                        blendshapeData[blendshapeName] = new List<(int, int, Mesh, int, Matrix4x4)>();
                    }
                    blendshapeData[blendshapeName].Add((vertexOffset, vertexCount, originalMesh, i, identityMatrix));
                }
                vertexOffset += vertexCount;
            }
            
            // Sixth pass: manually combine all mesh data into multiple submeshes (one per unique material)
            vertexOffset = 0;
            foreach (var (originalMesh, vertexCount, renderer, isSkinned, skin) in meshData) {
                // Get UDIM offset for this mesh
                // When explicitly specifying meshes, UDIM tile is calculated from order in the list
                var udimOffset = Vector2.zero;
                if (model.enableUdimMapping) {
                    if (model.meshSources.Count > 0) {
                        // Explicitly specified meshes: use index in list as UDIM tile
                        var sourceIndex = model.meshSources.FindIndex(s => s.obj == renderer.owner());
                        if (sourceIndex >= 0) {
                            // UDIM tiles are arranged in a 10x1 grid (0-9)
                            // Each tile is 1 unit wide, so tile N starts at X = N
                            udimOffset = new Vector2(sourceIndex, 0);
                        }
                    } else {
                        // Include children mode: use udimTile from meshSources if available
                        // (This shouldn't happen in include children mode, but keep for compatibility)
                        var source = model.meshSources.FirstOrDefault(s => s.obj == renderer.owner());
                        if (source != null) {
                            udimOffset = new Vector2(source.udimTile, 0);
                        }
                    }
                }
                
                // Get mesh data (GetMesh already handles making it readable if needed)
                var mesh = originalMesh;
                
                var vertices = mesh.vertices;
                var normals = mesh.normals.Length > 0 ? mesh.normals : new Vector3[vertices.Length];
                var tangents = mesh.tangents.Length > 0 ? mesh.tangents : new Vector4[vertices.Length];
                var uvs = mesh.uv.Length > 0 ? mesh.uv : new Vector2[vertices.Length];
                var colors = mesh.colors32.Length > 0 ? mesh.colors32 : new Color32[vertices.Length];
                
                // For skinned meshes, vertices are already in the correct space (relative to root bone or renderer)
                // For non-skinned meshes, we need to transform to target object's local space
                Matrix4x4 vertexTransform = Matrix4x4.identity;
                if (!isSkinned) {
                    // Transform non-skinned mesh vertices to target object's local space
                    var rendererTransform = renderer.owner();
                    var targetTransform = targetObject;
                    var localToWorld = rendererTransform.localToWorldMatrix;
                    var worldToLocal = targetTransform.worldToLocalMatrix;
                    vertexTransform = worldToLocal * localToWorld;
                }
                
                // Add vertices, normals, tangents, UVs, colors
                for (int i = 0; i < vertices.Length; i++) {
                    // Only transform if not skinned
                    if (isSkinned) {
                        allVertices.Add(vertices[i]);
                    } else {
                        allVertices.Add(vertexTransform.MultiplyPoint3x4(vertices[i]));
                    }
                    
                    if (i < normals.Length) {
                        if (isSkinned) {
                            allNormals.Add(normals[i]);
                        } else {
                            allNormals.Add(vertexTransform.MultiplyVector(normals[i]).normalized);
                        }
                    } else {
                        allNormals.Add(Vector3.up);
                    }
                    
                    if (i < tangents.Length) {
                        var tangent = tangents[i];
                        var tangentVec = new Vector3(tangent.x, tangent.y, tangent.z);
                        if (!isSkinned) {
                            tangentVec = vertexTransform.MultiplyVector(tangentVec).normalized;
                        }
                        allTangents.Add(new Vector4(tangentVec.x, tangentVec.y, tangentVec.z, tangent.w));
                    } else {
                        allTangents.Add(new Vector4(1, 0, 0, 1));
                    }
                    
                    // Apply UDIM offset to UVs
                    if (i < uvs.Length) {
                        allUvs.Add(uvs[i] + udimOffset);
                    } else {
                        allUvs.Add(Vector2.zero);
                    }
                    
                    // Only add vertex colors if they exist in the source mesh
                    // Don't add white colors if they don't exist, as this can affect lighting
                    if (i < colors.Length) {
                        allColors.Add(colors[i]);
                    }
                }
                
                // Handle bone weights - remap bone indices to combined bone array
                if (isSkinned && skin != null && mesh.boneWeights.Length > 0 && boneIndexMap.ContainsKey(skin)) {
                    var indexMap = boneIndexMap[skin];
                    var boneWeights = mesh.boneWeights;
                    
                    foreach (var bw in boneWeights) {
                        var remappedBw = new BoneWeight {
                            boneIndex0 = (bw.boneIndex0 >= 0 && indexMap.ContainsKey(bw.boneIndex0)) ? indexMap[bw.boneIndex0] : 0,
                            weight0 = bw.weight0,
                            boneIndex1 = (bw.boneIndex1 >= 0 && indexMap.ContainsKey(bw.boneIndex1)) ? indexMap[bw.boneIndex1] : 0,
                            weight1 = bw.weight1,
                            boneIndex2 = (bw.boneIndex2 >= 0 && indexMap.ContainsKey(bw.boneIndex2)) ? indexMap[bw.boneIndex2] : 0,
                            weight2 = bw.weight2,
                            boneIndex3 = (bw.boneIndex3 >= 0 && indexMap.ContainsKey(bw.boneIndex3)) ? indexMap[bw.boneIndex3] : 0,
                            weight3 = bw.weight3
                        };
                        allBoneWeights.Add(remappedBw);
                    }
                } else {
                    // Add empty bone weights for non-skinned meshes
                    for (int i = 0; i < vertices.Length; i++) {
                        allBoneWeights.Add(new BoneWeight());
                    }
                }
                
                // Add triangles per submesh/material (offset by current vertex count)
                // Each source mesh may have multiple submeshes, each with its own material
                var subMeshCount = mesh.subMeshCount;
                var rendererMaterials = renderer.sharedMaterials;
                
                for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++) {
                    // Get triangles for this submesh
                    var subMeshTriangles = mesh.GetTriangles(subMeshIndex);
                    
                    // Determine which material this submesh uses
                    Material subMeshMaterial = null;
                    if (subMeshIndex < rendererMaterials.Length && rendererMaterials[subMeshIndex] != null) {
                        subMeshMaterial = rendererMaterials[subMeshIndex];
                    } else if (rendererMaterials.Length > 0 && rendererMaterials[0] != null) {
                        // Fallback to first material if submesh index is out of range
                        subMeshMaterial = rendererMaterials[0];
                    }
                    
                    if (subMeshMaterial != null && materialToIndex.ContainsKey(subMeshMaterial)) {
                        var materialIndex = materialToIndex[subMeshMaterial];
                        
                        // Initialize triangle list for this material if needed
                        if (!trianglesByMaterial.ContainsKey(materialIndex)) {
                            trianglesByMaterial[materialIndex] = new List<int>();
                        }
                        
                        // Add triangles with vertex offset
                        foreach (var triangle in subMeshTriangles) {
                            trianglesByMaterial[materialIndex].Add(triangle + vertexOffset);
                        }
                    }
                }
                
                vertexOffset += vertexCount;
            }
            
            // Build combined mesh manually with multiple submeshes
            combinedMesh.vertices = allVertices.ToArray();
            combinedMesh.normals = allNormals.ToArray();
            combinedMesh.tangents = allTangents.ToArray();
            combinedMesh.uv = allUvs.ToArray();
            
            // Set up submeshes (one per unique material)
            combinedMesh.subMeshCount = uniqueMaterials.Count;
            for (int i = 0; i < uniqueMaterials.Count; i++) {
                if (trianglesByMaterial.ContainsKey(i) && trianglesByMaterial[i].Count > 0) {
                    combinedMesh.SetTriangles(trianglesByMaterial[i].ToArray(), i);
                } else {
                    // Empty submesh if no triangles for this material
                    combinedMesh.SetTriangles(new int[0], i);
                }
            }
            
            // Only set vertex colors if ALL source meshes had them (don't add colors if any mesh was missing them)
            // This prevents accidentally adding white colors which can darken the appearance
            var allMeshesHadColors = meshData.All(md => {
                var mesh = md.originalMesh;
                return mesh.colors32 != null && mesh.colors32.Length == mesh.vertexCount;
            });
            if (allMeshesHadColors && allColors.Count == allVertices.Count) {
                combinedMesh.colors32 = allColors.ToArray();
            }
            
            if (allBoneWeights.Count > 0 && allBoneWeights.Any(bw => bw.weight0 > 0)) {
                combinedMesh.boneWeights = allBoneWeights.ToArray();
            }
            
            // Recalculate normals and tangents after combining to ensure proper lighting
            // This is especially important when combining meshes from different transforms
            combinedMesh.RecalculateNormals();
            combinedMesh.RecalculateTangents();
            combinedMesh.RecalculateBounds();
            combinedMesh.ForceReadable();
            
            // Rebuild blendshapes on combined mesh
            foreach (var kvp in blendshapeData) {
                var blendshapeName = kvp.Key;
                var sources = kvp.Value;
                
                // Get total vertex count
                var totalVertexCount = combinedMesh.vertexCount;
                var deltaVertices = new Vector3[totalVertexCount];
                var deltaNormals = new Vector3[totalVertexCount];
                var deltaTangents = new Vector3[totalVertexCount];
                
                // Combine blendshape deltas from all source meshes
                foreach (var (offset, count, sourceMesh, blendshapeIndex, transformMatrix) in sources) {
                    var sourceDeltas = new Vector3[count];
                    var sourceNormals = new Vector3[count];
                    var sourceTangents = new Vector3[count];
                    
                    // Get the last frame (usually weight 100)
                    var frameCount = sourceMesh.GetBlendShapeFrameCount(blendshapeIndex);
                    var lastFrameIndex = frameCount - 1;
                    sourceMesh.GetBlendShapeFrameVertices(blendshapeIndex, lastFrameIndex, sourceDeltas, sourceNormals, sourceTangents);
                    
                    // Transform and copy deltas to combined mesh
                    for (int i = 0; i < count && (offset + i) < totalVertexCount; i++) {
                        deltaVertices[offset + i] = transformMatrix.MultiplyVector(sourceDeltas[i]);
                        deltaNormals[offset + i] = transformMatrix.MultiplyVector(sourceNormals[i]).normalized;
                        deltaTangents[offset + i] = transformMatrix.MultiplyVector(sourceTangents[i]).normalized;
                    }
                }
                
                // Add blendshape frame to combined mesh
                combinedMesh.AddBlendShapeFrame(blendshapeName, 100f, deltaVertices, deltaNormals, deltaTangents);
            }
            
            // Create or get target renderer
            SkinnedMeshRenderer targetRenderer = targetObject.GetComponent<SkinnedMeshRenderer>();
            if (targetRenderer == null) {
                // Remove existing MeshRenderer/MeshFilter if present
                var existingMeshRenderer = targetObject.GetComponent<MeshRenderer>();
                var existingMeshFilter = targetObject.GetComponent<MeshFilter>();
                if (existingMeshRenderer != null) {
                    UnityEngine.Object.DestroyImmediate(existingMeshRenderer);
                }
                if (existingMeshFilter != null) {
                    UnityEngine.Object.DestroyImmediate(existingMeshFilter);
                }
                
                targetRenderer = targetObject.AddComponent<SkinnedMeshRenderer>();
            }
            
            // Assign combined mesh and materials (one material per submesh)
            targetRenderer.sharedMesh = combinedMesh;
            targetRenderer.sharedMaterials = uniqueMaterials.ToArray();
            
            // Set up bones and bindposes for skinned mesh
            if (allBones.Count > 0) {
                targetRenderer.bones = allBones.ToArray();
                combinedMesh.bindposes = allBindPoses.ToArray();
                
                // Set root bone from first skinned mesh renderer if available
                var firstSkinned = renderers.OfType<SkinnedMeshRenderer>().FirstOrDefault();
                if (firstSkinned != null && firstSkinned.rootBone != null) {
                    targetRenderer.rootBone = firstSkinned.rootBone;
                } else if (allBones.Count > 0) {
                    // Fallback: use first bone as root
                    targetRenderer.rootBone = allBones[0];
                }
            }
            
            // Copy renderer settings from first source renderer to maintain lighting
            var firstRenderer = renderers.FirstOrDefault();
            if (firstRenderer != null) {
                targetRenderer.lightProbeUsage = firstRenderer.lightProbeUsage;
                targetRenderer.reflectionProbeUsage = firstRenderer.reflectionProbeUsage;
                targetRenderer.shadowCastingMode = firstRenderer.shadowCastingMode;
                targetRenderer.receiveShadows = firstRenderer.receiveShadows;
                targetRenderer.motionVectorGenerationMode = firstRenderer.motionVectorGenerationMode;
                targetRenderer.sortingLayerID = firstRenderer.sortingLayerID;
                targetRenderer.sortingOrder = firstRenderer.sortingOrder;
                
                // Copy light probe anchor override
                if (firstRenderer.probeAnchor != null) {
                    targetRenderer.probeAnchor = firstRenderer.probeAnchor;
                }
                
                // Copy SkinnedMeshRenderer-specific settings
                if (firstRenderer is SkinnedMeshRenderer firstSkinRenderer) {
                    targetRenderer.skinnedMotionVectors = firstSkinRenderer.skinnedMotionVectors;
                    targetRenderer.lightProbeProxyVolumeOverride = firstSkinRenderer.lightProbeProxyVolumeOverride;
                    targetRenderer.updateWhenOffscreen = firstSkinRenderer.updateWhenOffscreen;
                    targetRenderer.allowOcclusionWhenDynamic = firstSkinRenderer.allowOcclusionWhenDynamic;
                    targetRenderer.quality = firstSkinRenderer.quality;
                    
                    // Copy bounds if they were set
                    if (firstSkinRenderer.localBounds.size != Vector3.zero) {
                        targetRenderer.localBounds = firstSkinRenderer.localBounds;
                    }
                }
            }
            
            // Apply blendshape weights from source renderers to combined renderer
            // Track conflicts where multiple renderers have the same blendshape name
            var blendshapeConflicts = new Dictionary<string, List<(VFGameObject obj, float weight)>>();
            
            Debug.Log($"MeshCombiner: Applying blendshape weights from {renderers.Count} renderers to {targetObject.GetPath(avatarObject)}");
            
            foreach (var renderer in renderers) {
                if (renderer is SkinnedMeshRenderer sourceSkin && sourceSkin.sharedMesh != null) {
                    var sourceMesh = sourceSkin.sharedMesh;
                    var rendererObj = renderer.owner();
                    var rendererPath = rendererObj.GetPath(avatarObject);
                    
                    Debug.Log($"MeshCombiner: Processing renderer {rendererPath} with {sourceMesh.blendShapeCount} blendshapes");
                    
                    for (int i = 0; i < sourceMesh.blendShapeCount; i++) {
                        var blendshapeName = sourceMesh.GetBlendShapeName(i);
                        var sourceWeight = sourceSkin.GetBlendShapeWeight(i);
                        
                        // Find the blendshape in the combined mesh
                        var combinedBlendshapeIndex = combinedMesh.GetBlendShapeIndex(blendshapeName);
                        if (combinedBlendshapeIndex >= 0) {
                            // Track this blendshape application for conflict detection (track all, even if weight is 0)
                            // The conflict is about having the same blendshape name, not about the weight value
                            if (!blendshapeConflicts.ContainsKey(blendshapeName)) {
                                blendshapeConflicts[blendshapeName] = new List<(VFGameObject, float)>();
                            }
                            blendshapeConflicts[blendshapeName].Add((rendererObj, sourceWeight));
    
                            // Note: If multiple renderers have the same blendshape, later ones will overwrite earlier ones
                            Debug.Log($"MeshCombiner:     Applying weight {sourceWeight} to combined blendshape '{blendshapeName}' (index {combinedBlendshapeIndex})");
                            targetRenderer.SetBlendShapeWeight(combinedBlendshapeIndex, sourceWeight);
                        } else {
                            Debug.LogWarning($"MeshCombiner:     Blendshape '{blendshapeName}' from {rendererPath} not found in combined mesh!");
                        }
                    }
                }
            }
            
            // Warn about conflicts where multiple renderers have the same blendshape name with different weights
            var conflicts = blendshapeConflicts
                .Where(kvp => {
                    // Only warn if multiple renderers have this blendshape AND they have different weights
                    if (kvp.Value.Count <= 1) return false;
                    var weights = kvp.Value.Select(v => v.weight).Distinct().ToList();
                    return weights.Count > 1; // Different weights = actual conflict
                })
                .ToList();
            if (conflicts.Count > 0) {
                var conflictMessages = conflicts.Select(kvp => {
                    var blendshapeName = kvp.Key;
                    var sources = kvp.Value;
                    var sourceList = sources.Select(s => $"  • {s.obj.GetPath(avatarObject)} (weight: {s.weight})").Join('\n');
                    return $"Blendshape '{blendshapeName}' has different weights on multiple objects:\n{sourceList}\n" +
                           $"The last object's weight ({sources.Last().weight}) will be used.";
                });
                
                var message = "MeshCombiner: Multiple source meshes have blendshapes with the same name.\n\n" +
                             "When combining meshes, if multiple source renderers have the same blendshape name, " +
                             "the weight from the last renderer will overwrite earlier ones.\n\n" +
                             "Conflicts found:\n\n" +
                             conflictMessages.Join("\n\n");
                
                // Log to console
                Debug.LogWarning($"MeshCombiner: Blendshape name conflicts detected on {targetObject.GetPath(avatarObject)}\n\n{message}");
                
                // Show dialog
                DialogUtils.DisplayDialog(
                    "MeshCombiner: Blendshape Name Conflicts",
                    message,
                    "Ok"
                );
            }
            
            // Hide source renderers (non-destructive - they'll be removed by EditorOnly system)
            foreach (var renderer in renderers) {
                if (renderer != targetRenderer) {
                    renderer.enabled = false;
                    // Optionally mark objects for deletion during upload
                    var owner = renderer.owner();
                    // User can manually add DeleteDuringUpload if they want to remove these
                }
            }
            
            VRCFuryEditorUtils.MarkDirty(targetRenderer);
            VRCFuryEditorUtils.MarkDirty(targetObject);
            
            Debug.Log($"MeshCombiner: Combined {renderers.Count} meshes into {targetObject.GetPath(avatarObject)}");
        }
        
        [FeatureEditor]
        public static VisualElement Editor(SerializedProperty prop, VFGameObject avatarObject, VFGameObject componentObject, MeshCombiner model) {
            var content = new VisualElement();
            
            var includeChildrenProp = prop.FindPropertyRelative("includeChildren");
            var meshSourcesProp = prop.FindPropertyRelative("meshSources");
            var enableUdimMappingProp = prop.FindPropertyRelative("enableUdimMapping");
            
            content.Add(VRCFuryEditorUtils.Prop(includeChildrenProp, "Include Children", 
                tooltip: "If enabled, combines all renderers in children. If disabled, only combines explicitly specified meshes."));
            
            content.Add(VRCFuryEditorUtils.RefreshOnChange(() => {
                var c = new VisualElement();
                if (!includeChildrenProp.boolValue) {
                    c.Add(VRCFuryEditorUtils.WrappedLabel("Specify meshes to combine:"));
                    var udimLabel = VRCFuryEditorUtils.WrappedLabel("UDIM tiles will be assigned automatically based on order (first = 0, second = 1, etc.)");
                    udimLabel.style.fontSize = 11;
                    c.Add(udimLabel);
                    
                    // Custom list that hides udimTile field - only show obj field
#if UNITY_2022_1_OR_NEWER
                    var listContainer = new VisualElement();
                    listContainer.AddToClassList("vfList");
                    
                    var listView = new ListView();
                    listView.AddToClassList("vfList__listView");
                    listView.reorderable = true;
                    listView.reorderMode = ListViewReorderMode.Animated;
                    listView.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
                    listView.showBorder = true;
                    listView.showAddRemoveFooter = false;
                    listView.bindingPath = meshSourcesProp.propertyPath;
                    listView.showBoundCollectionSize = false;
                    listView.selectionType = SelectionType.None;
                    listView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
                    
                    listView.makeItem = () => {
                        var container = new VisualElement();
                        var objField = new ObjectField("Obj") { objectType = typeof(GameObject) };
                        objField.AddToClassList("vfList__item");
                        container.Add(objField);
                        return container;
                    };
                    
                    listView.bindItem = (element, i) => {
                        var objField = element.Q<ObjectField>();
                        var elementProp = meshSourcesProp.GetArrayElementAtIndex(i);
                        var objProp = elementProp.FindPropertyRelative("obj");
                        objField.bindingPath = objProp.propertyPath;
                        objField.Bind(meshSourcesProp.serializedObject);
                    };
                    
                    listView.unbindItem = (element, i) => {
                        element.Unbind();
                    };
                    
                    var footer = new VisualElement() { name = BaseListView.footerUssClassName };
                    footer.AddToClassList(BaseListView.footerUssClassName);
                    footer.Add(new Button(() => {
                        meshSourcesProp.arraySize++;
                        meshSourcesProp.serializedObject.ApplyModifiedProperties();
                    }) { text = "+" });
                    footer.Add(new Button(() => {
                        if (meshSourcesProp.arraySize > 0) {
                            meshSourcesProp.DeleteArrayElementAtIndex(meshSourcesProp.arraySize - 1);
                            meshSourcesProp.serializedObject.ApplyModifiedProperties();
                        }
                    }) { text = "-" });
                    
                    listContainer.Add(listView);
                    listContainer.Add(footer);
                    c.Add(listContainer);
#else
                    // Fallback for older Unity versions - use standard list but hide udimTile via CSS
                    var listEl = VRCFuryEditorUtils.Prop(meshSourcesProp, "Mesh Sources");
                    // Hide udimTile fields using style
                    listEl.schedule.Execute(() => {
                        var udimFields = listEl.Query<PropertyField>().ToList();
                        foreach (var field in udimFields) {
                            var prop = field.bindingPath;
                            if (prop != null && prop.Contains("udimTile")) {
                                field.style.display = DisplayStyle.None;
                            }
                        }
                    });
                    c.Add(listEl);
#endif
                }
                return c;
            }, includeChildrenProp));
            
            content.Add(VRCFuryEditorUtils.Prop(enableUdimMappingProp, "Enable UDIM Mapping", 
                tooltip: "If enabled, applies UDIM UV offsets to each mesh. When explicitly specifying meshes, UDIM tiles are assigned automatically based on order."));
            
            content.Add(VRCFuryEditorUtils.RefreshOnChange(() => {
                var c = new VisualElement();
                if (enableUdimMappingProp.boolValue) {
                    if (!includeChildrenProp.boolValue) {
                        c.Add(VRCFuryEditorUtils.WrappedLabel("UDIM tiles are assigned automatically: first mesh = tile 0 (UDIM 1001), second = tile 1 (UDIM 1002), etc."));
                    } else {
                        c.Add(VRCFuryEditorUtils.WrappedLabel("UDIM tiles: 0 = UDIM 1001, 1 = UDIM 1002, etc."));
                        c.Add(VRCFuryEditorUtils.WrappedLabel("Each mesh will have its UV coordinates offset by the tile index."));
                    }
                }
                return c;
            }, enableUdimMappingProp, includeChildrenProp));
            
            // Show preview of what will be combined
            content.Add(VRCFuryEditorUtils.Debug(refreshElement: () => {
                var preview = new VisualElement();
                preview.Add(VRCFuryEditorUtils.WrappedLabel("Will combine:").Bold());
                
                var renderers = new List<Renderer>();
                if (model.meshSources.Count > 0) {
                    foreach (var source in model.meshSources) {
                        if (source.obj != null) {
                            var renderer = source.obj.GetComponent<Renderer>();
                            if (renderer != null) {
                                renderers.Add(renderer);
                            }
                        }
                    }
                } else if (model.includeChildren) {
                    renderers.AddRange(componentObject.GetComponentsInSelfAndChildren<Renderer>());
                } else {
                    foreach (var child in componentObject.Children()) {
                        var renderer = child.GetComponent<Renderer>();
                        if (renderer != null) {
                            renderers.Add(renderer);
                        }
                    }
                }
                
                var validRenderers = renderers
                    .Where(r => {
                        if (r is SkinnedMeshRenderer skin) return skin.sharedMesh != null;
                        if (r is MeshRenderer) {
                            var filter = r.owner().GetComponent<MeshFilter>();
                            return filter != null && filter.sharedMesh != null;
                        }
                        return false;
                    })
                    .ToList();
                
                if (validRenderers.Count == 0) {
                    preview.Add(VRCFuryEditorUtils.Warn("No valid renderers found to combine."));
                } else {
                    foreach (var renderer in validRenderers) {
                        var mesh = renderer.GetMesh();
                        var meshName = mesh != null ? mesh.name : "No mesh";
                        var vertexCount = mesh != null ? mesh.vertexCount : 0;
                        
                        // Get path - try relative to componentObject first, fallback to full path if not a child
                        string path;
                        try {
                            var rendererObj = renderer.owner();
                            if (rendererObj.IsChildOf(componentObject)) {
                                path = rendererObj.GetPath(componentObject);
                            } else {
                                // Not a child, use full path from avatar root or just the object name
                                path = rendererObj.GetPath(avatarObject);
                            }
                        } catch {
                            // Fallback to just the object name if path calculation fails
                            path = renderer.owner().name;
                        }
                        
                        var udimInfo = "";
                        if (model.enableUdimMapping) {
                            if (model.meshSources.Count > 0) {
                                // Explicitly specified meshes: show calculated UDIM tile from order
                                var sourceIndex = model.meshSources.FindIndex(s => s.obj == renderer.owner());
                                if (sourceIndex >= 0) {
                                    udimInfo = $" (UDIM tile {sourceIndex})";
                                }
                            } else {
                                // Include children mode: show udimTile from source if available
                                var source = model.meshSources.FirstOrDefault(s => s.obj == renderer.owner());
                                if (source != null) {
                                    udimInfo = $" (UDIM tile {source.udimTile})";
                                }
                            }
                        }
                        
                        preview.Add(VRCFuryEditorUtils.WrappedLabel($"  • {path}: {meshName} ({vertexCount} vertices){udimInfo}"));
                    }
                }
                
                return preview;
            }));
            
            return content;
        }
    }
}

