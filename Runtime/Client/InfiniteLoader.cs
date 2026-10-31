using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Caelix
{
    /// <summary>
    /// Abstract base class for implementing infinite voxel world loading systems.
    /// Manages loading and unloading of regions based on a center point and configurable radius.
    /// </summary>
    /// <remarks>
    /// This component automatically loads regions near the load center and unloads distant regions.
    /// Regions are loaded in order of Manhattan distance from the center for optimal streaming.
    /// Derived classes must implement LoadRegion to define how region data is generated or loaded.
    /// </remarks>
    [RequireComponent(typeof(VoxelEntity))]
    public abstract class InfiniteLoader : MonoBehaviour
    {
        /// <summary>
        /// The transform whose position determines which regions to load.
        /// Typically set to the player's transform.
        /// </summary>
        public Transform loadCenter;

        /// <summary>
        /// Called to load a region at the specified position.
        /// Must be implemented by derived classes to define loading behavior.
        /// </summary>
        /// <param name="regionPos">The region position to load, in region coordinates.</param>
        public abstract void LoadRegion(int3 regionPos);

        /// <summary>
        /// The voxel entity that owns the loaded regions.
        /// </summary>
        protected VoxelEntity entity;

        private List<int3> sectorLoadOrder = new();
        private bool initialized;

        /// <summary>
        /// Maximum bounds for region loading in each axis, in region units. Serialized field name
        /// kept: scene data depends on it.
        /// </summary>
        public int3 sectorLoadBounds;

        /// <summary>
        /// Radius in blocks for loading regions (converted to region units internally). Serialized
        /// field name kept: scene data depends on it.
        /// </summary>
        public float sectorLoadRadiusInBlocks;

        /// <summary>
        /// Radius in blocks for unloading regions. Should be larger than the load radius to prevent
        /// thrashing. Serialized field name kept: scene data depends on it.
        /// </summary>
        public float sectorUnloadRadiusInBlocks;

        /// <summary>
        /// Set of region positions currently being loaded to prevent duplicate load requests.
        /// </summary>
        protected HashSet<int3> loadingSectors = new();

        /// <summary>
        /// Initializes the loader by finding the VoxelEntity component and calculating load order.
        /// </summary>
        // Streaming is not driven by the world tick in v1 (see SERVER_CLIENT_ARCHITECTURE.md).
        // Call Tick() from your own loop. The loader writes into server data through the
        // entity component, so it only works in a process that runs the server.
        protected virtual void OnEnable()
        {
            InitializeLoader();
        }

        protected virtual void Start()
        {
            InitializeLoader();
        }

        protected void InitializeLoader()
        {
            if (initialized)
            {
                return;
            }

            entity = gameObject.GetComponent<VoxelEntity>();
            ResetSectorLoadOrder();
            initialized = true;
        }

        /// <summary>
        /// Gets the current region position of the load center.
        /// </summary>
        /// <remarks>
        /// Divides the load center's world position by the region's brick count (16) to convert
        /// from world space to region space. Note: this divides by the brick count instead of the
        /// region's block size (128), which means the region positions returned are scaled by a
        /// factor of 8. Kept exactly as it was, to preserve the streaming behaviour.
        /// </remarks>
        public int3 loadCenterSectorPos => (int3)math.floor(loadCenter.position / VoxelRegion.SizeInBricks);

        /// <summary>
        /// Determines whether a region should be unloaded based on its distance from the center.
        /// </summary>
        /// <param name="sectorPos">The region position to check.</param>
        /// <param name="centerSectorPos">The current center region position.</param>
        /// <returns>True if the region is outside the unload radius or bounds.</returns>
        public bool ShouldUnload(int3 sectorPos, int3 centerSectorPos)
        {
            int3 relativePos = sectorPos - loadCenterSectorPos;
            relativePos.y = 0;
            return !((math.length(relativePos) * VoxelRegion.SizeInBlocks) <= sectorUnloadRadiusInBlocks
                && math.abs(relativePos.x) <= sectorLoadBounds.x
                && math.abs(relativePos.y) <= sectorLoadBounds.y
                && math.abs(relativePos.z) <= sectorLoadBounds.z);
        }

        /// <summary>
        /// Recalculates the region loading order based on current bounds and radius settings.
        /// </summary>
        /// <remarks>
        /// This should be called whenever sectorLoadBounds or sectorLoadRadiusInBlocks changes.
        /// The load order is cached for efficiency and reused each tick.
        /// </remarks>
        public void ResetSectorLoadOrder()
        {
            // Fill region load order list
            SectorLoadGeometry.GeneratePointsInIntersection(sectorLoadBounds, sectorLoadRadiusInBlocks / VoxelRegion.SizeInBlocks, ref sectorLoadOrder);
        }

        /// <summary>
        /// Performs one update tick: unloads distant regions and loads nearby regions.
        /// </summary>
        /// <remarks>
        /// This method first unloads regions outside the unload radius, then loads regions
        /// within the load radius that aren't already loaded or being loaded.
        /// TODO: Split region loading across multiple frames for better performance.
        /// </remarks>
        public virtual void Tick()
        {
            // The loader writes server data through the entity; without it there is nothing to stream.
            if (entity == null || !entity.HasServerData) return;

            int3 lsp = loadCenterSectorPos;

            // Unload regions
            var list = entity.GetRegionPositions(Allocator.Temp);
            foreach (var _sectorPos in list)
            {
                int3 sectorPos = new int3(_sectorPos.x, _sectorPos.y, _sectorPos.z);
                if (ShouldUnload(sectorPos, lsp))
                {
                    entity.RemoveRegion(sectorPos);
                }
            }

            list.Dispose();

            // Load regions
            // TODO: Split to frames
            for (int currentIndex = 0; currentIndex < sectorLoadOrder.Count; currentIndex++)
            {
                int3 targetSectorPos = lsp + sectorLoadOrder[currentIndex];
                // Debug.Log($"Loaded region @ {targetSectorPos}");
                if (loadingSectors.Contains(targetSectorPos) || entity.HasRegion(targetSectorPos))
                {
                    continue;
                }

                loadingSectors.Add(targetSectorPos);
                LoadRegion(targetSectorPos);
            }
        }

        /// <summary>
        /// Marks a region as fully loaded and hands its storage to the entity.
        /// Should be called by derived classes when <see cref="LoadRegion"/> completes.
        /// </summary>
        /// <param name="region">The DETACHED region that has finished loading.</param>
        public unsafe void MarkRegionLoaded(ref VoxelRegion region)
        {
            Debug.Log($"Region Added at {region.RegionPos}");
            loadingSectors.Remove(region.RegionPos);
            entity.AttachRegion(ref region);
        }
    }
}
