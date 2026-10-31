using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Caelix.Authoring
{
    /// <summary>
    /// Base component for editable scene tools that generate a temporary voxel entity.
    /// The working entity stays in the Unity scene, is excluded from .vxw saves, and can be
    /// baked to a regular entity or merged into a grid-aligned entity.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VoxelEntity), typeof(VoxelBody))]
    public abstract class VoxelEntityAuthoringTool : MonoBehaviour
    {
        private const float GridAlignmentTolerance = 0.001f;

        [SerializeField] private VoxelEntity bakeTarget;
        [SerializeField, HideInInspector] private int generatedVoxelCount;

        private readonly HashSet<Vector3Int> voxelScratch = new();
        private bool rebuildRequested = true;

        public VoxelEntity OwnedEntity => GetComponent<VoxelEntity>();

        public VoxelEntity BakeTarget
        {
            get => bakeTarget;
            set => bakeTarget = value;
        }

        public int GeneratedVoxelCount => generatedVoxelCount;

        protected abstract Block MaterialBlock { get; }

        protected virtual void Reset()
        {
            ConfigureOwnedComponents();
        }

        protected virtual void Awake()
        {
            ConfigureOwnedComponents();
        }

        protected virtual void OnEnable()
        {
            ConfigureOwnedComponents();
            rebuildRequested = true;
        }

        protected virtual void Start()
        {
            RebuildNow();
        }

        protected virtual void Update()
        {
            if (rebuildRequested && Application.isPlaying)
            {
                RebuildNow();
            }
        }

        /// <summary>
        /// Whether the working entity can accept generated voxels right now.
        /// </summary>
        /// <remarks>
        /// <see cref="VoxelEntity"/> registers its entity with the server in OnEnable and removes it
        /// in OnDisable, so the component can be alive while its storage is gone — a destroyed or
        /// disabled entity, an undo in Play Mode, or a rebuild scheduled before Awake ran. Writing
        /// then reaches disposed native memory and throws a NullReferenceException from inside
        /// Unity.Collections, which is why every entry point checks this first.
        /// </remarks>
        public bool CanWriteVoxels
        {
            get
            {
                VoxelEntity entity = OwnedEntity;
                return entity != null && entity.HasServerData;
            }
        }

#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            ConfigureOwnedComponents();
            RequestRebuild();
        }
#endif

        /// <summary>Schedules one rebuild on the next update.</summary>
        public void RequestRebuild()
        {
            rebuildRequested = true;
        }

        /// <summary>
        /// Replaces the working entity's visible blocks with the current authored result.
        /// Native voxel data exists only in Play Mode, so edit mode uses the Scene preview.
        /// </summary>
        public bool RebuildNow()
        {
            rebuildRequested = false;
            ConfigureOwnedComponents();

            if (!Application.isPlaying || !CanWriteVoxels)
            {
                return false;
            }

            VoxelEntity entity = OwnedEntity;

            try
            {
                voxelScratch.Clear();
                BuildVoxelSet(voxelScratch);

                ClearBlocks(entity);

                Block block = MaterialBlock;
                if (!block.isEmpty)
                {
                    foreach (Vector3Int position in voxelScratch)
                    {
                        entity.SetBlock(new int3(position.x, position.y, position.z), block);
                    }
                }

                entity.RefreshAllocatedBrickLists();
                generatedVoxelCount = block.isEmpty ? 0 : voxelScratch.Count;
                return true;
            }
            catch (Exception exception)
            {
                // One malformed spline must not take down the frame loop; the tool keeps working
                // and the next edit tries again.
                Debug.LogException(exception, this);
                return false;
            }
            finally
            {
                voxelScratch.Clear();
            }
        }

        /// <summary>Creates a regular, saveable entity with a copy of the generated voxels.</summary>
        public VoxelEntity BakeCopy()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Voxel authoring tools can bake only in Play Mode.", this);
                return null;
            }

            if (!CanWriteVoxels)
            {
                Debug.LogWarning("This tool's voxel entity is not available; nothing to bake.", this);
                return null;
            }

            RebuildNow();

            var bakedObject = new GameObject($"{name} (Baked)");
            bakedObject.SetActive(false);

            Scene sourceScene = gameObject.scene;
            if (sourceScene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(bakedObject, sourceScene);
            }

            bakedObject.transform.SetPositionAndRotation(transform.position, transform.rotation);
            bakedObject.transform.localScale = Vector3.one;

            VoxelEntity bakedEntity = bakedObject.AddComponent<VoxelEntity>();
            VoxelBody bakedBody = bakedObject.AddComponent<VoxelBody>();
            bakedBody.accuratePhysics = true;

            bakedObject.SetActive(true);
            bakedEntity.IsStatic = true;
            bakedEntity.ExcludeFromWorldSave = false;

            CopyNonEmptyBlocks(OwnedEntity, bakedEntity, null);
            bakedEntity.RefreshAllocatedBrickLists();
            return bakedEntity;
        }

        /// <summary>
        /// Merges generated blocks into <see cref="BakeTarget"/>. The relative transform must map
        /// integer voxel coordinates to integer voxel coordinates without scale or skew.
        /// Existing target blocks at those coordinates are replaced.
        /// </summary>
        public bool BakeIntoTarget(out string error, out int copiedVoxelCount)
        {
            copiedVoxelCount = 0;

            if (!Application.isPlaying)
            {
                error = "Voxel authoring tools can bake only in Play Mode.";
                return false;
            }

            if (bakeTarget == null)
            {
                error = "Select a Bake Target entity first.";
                return false;
            }

            if (bakeTarget == OwnedEntity)
            {
                error = "The working entity cannot be its own Bake Target.";
                return false;
            }

            if (!CanWriteVoxels)
            {
                error = "This tool's voxel entity is not available. Re-enter Play Mode and try again.";
                return false;
            }

            if (!bakeTarget.HasServerData)
            {
                error = "The Bake Target entity has no voxel storage. Make sure it is active and enabled.";
                return false;
            }

            if (!TryGetGridAlignedMapping(transform, bakeTarget.transform, out Matrix4x4 mapping, out error))
            {
                return false;
            }

            RebuildNow();
            copiedVoxelCount = CopyNonEmptyBlocks(OwnedEntity, bakeTarget, mapping);
            bakeTarget.RefreshAllocatedBrickLists();
            error = null;
            return true;
        }

        /// <summary>
        /// Adds every serialized component that defines this tool's editable scene state.
        /// Editor persistence stores these components when the user keeps Play Mode changes.
        /// </summary>
        public virtual void CollectAuthoringStateComponents(List<Component> components)
        {
            components.Add(this);
        }

        protected abstract void BuildVoxelSet(HashSet<Vector3Int> voxels);

        public void ConfigureOwnedComponents()
        {
            VoxelEntity entity = GetComponent<VoxelEntity>();
            if (entity != null)
            {
                entity.ExcludeFromWorldSave = true;
                entity.IsStatic = true;
            }

            VoxelBody body = GetComponent<VoxelBody>();
            if (body != null)
            {
                body.accuratePhysics = true;
            }
        }

        /// <remarks>
        /// <see cref="VoxelEntity.SetBlock"/> writes into the storage the enumerator is walking.
        /// That is safe here for the same reason it was before the facade: clearing a voxel never
        /// removes a brick, so the set of allocated bricks does not change under the enumerator.
        /// </remarks>
        private static unsafe void ClearBlocks(VoxelEntity entity)
        {
            if (entity == null || !entity.HasServerData)
            {
                return;
            }

            VoxelEntityData data = entity.ServerData;
            foreach (int3 key in data.EnumerateBricks())
            {
                if (!data.TryBindBrick(SectorSlotId.Block, key, out Block* blocks))
                {
                    continue;
                }

                int3 origin = BrickKey.ToBlockOrigin(key);
                for (int z = 0; z < BrickKey.BlocksPerAxis; z++)
                for (int y = 0; y < BrickKey.BlocksPerAxis; y++)
                for (int x = 0; x < BrickKey.BlocksPerAxis; x++)
                {
                    if (blocks[BrickKey.ToBlockIdx(x, y, z)].isEmpty)
                    {
                        continue;
                    }

                    entity.SetBlock(origin + new int3(x, y, z), Block.Empty);
                }
            }
        }

        private static unsafe int CopyNonEmptyBlocks(
            VoxelEntity source,
            VoxelEntity destination,
            Matrix4x4? sourceToDestination)
        {
            int copied = 0;
            if (source == null || destination == null ||
                !source.HasServerData || !destination.HasServerData)
            {
                return 0;
            }

            VoxelEntityData data = source.ServerData;
            foreach (int3 key in data.EnumerateBricks())
            {
                if (!data.TryBindBrick(SectorSlotId.Block, key, out Block* blocks))
                {
                    continue;
                }

                int3 origin = BrickKey.ToBlockOrigin(key);
                for (int z = 0; z < BrickKey.BlocksPerAxis; z++)
                for (int y = 0; y < BrickKey.BlocksPerAxis; y++)
                for (int x = 0; x < BrickKey.BlocksPerAxis; x++)
                {
                    Block block = blocks[BrickKey.ToBlockIdx(x, y, z)];
                    if (block.isEmpty)
                    {
                        continue;
                    }

                    int3 sourcePosition = origin + new int3(x, y, z);
                    int3 destinationPosition = sourcePosition;
                    if (sourceToDestination.HasValue)
                    {
                        Vector3 mapped = sourceToDestination.Value.MultiplyPoint3x4(
                            new Vector3(sourcePosition.x, sourcePosition.y, sourcePosition.z));
                        Vector3Int rounded = Vector3Int.RoundToInt(mapped);
                        destinationPosition = new int3(rounded.x, rounded.y, rounded.z);
                    }

                    destination.SetBlock(destinationPosition, block);
                    copied++;
                }
            }

            return copied;
        }

        public static bool TryGetGridAlignedMapping(
            Transform source,
            Transform destination,
            out Matrix4x4 sourceToDestination,
            out string error)
        {
            sourceToDestination = destination.worldToLocalMatrix * source.localToWorldMatrix;

            if (!IsUnitScale(source.lossyScale) || !IsUnitScale(destination.lossyScale))
            {
                error = "Both entity transforms must have a world scale of (1, 1, 1).";
                return false;
            }

            Vector3 x = sourceToDestination.MultiplyVector(Vector3.right);
            Vector3 y = sourceToDestination.MultiplyVector(Vector3.up);
            Vector3 z = sourceToDestination.MultiplyVector(Vector3.forward);
            if (!IsSignedAxis(x) || !IsSignedAxis(y) || !IsSignedAxis(z))
            {
                error = "The source rotation must align with the target voxel axes.";
                return false;
            }

            Vector3Int rx = Vector3Int.RoundToInt(x);
            Vector3Int ry = Vector3Int.RoundToInt(y);
            Vector3Int rz = Vector3Int.RoundToInt(z);
            if (Vector3.Dot(rx, ry) != 0f || Vector3.Dot(rx, rz) != 0f || Vector3.Dot(ry, rz) != 0f)
            {
                error = "The source rotation does not form three distinct target voxel axes.";
                return false;
            }

            Vector3 translation = sourceToDestination.MultiplyPoint3x4(Vector3.zero);
            if ((translation - (Vector3)Vector3Int.RoundToInt(translation)).sqrMagnitude >
                GridAlignmentTolerance * GridAlignmentTolerance)
            {
                error = "The source origin must align with an integer voxel coordinate in the target.";
                return false;
            }

            error = null;
            return true;
        }

        private static bool IsUnitScale(Vector3 scale)
        {
            return Mathf.Abs(scale.x - 1f) <= GridAlignmentTolerance &&
                   Mathf.Abs(scale.y - 1f) <= GridAlignmentTolerance &&
                   Mathf.Abs(scale.z - 1f) <= GridAlignmentTolerance;
        }

        private static bool IsSignedAxis(Vector3 axis)
        {
            Vector3Int rounded = Vector3Int.RoundToInt(axis);
            int magnitude = Mathf.Abs(rounded.x) + Mathf.Abs(rounded.y) + Mathf.Abs(rounded.z);
            return magnitude == 1 &&
                   (axis - (Vector3)rounded).sqrMagnitude <= GridAlignmentTolerance * GridAlignmentTolerance;
        }
    }
}
