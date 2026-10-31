using Unity.Mathematics;
using UnityEngine;
using Caelix.Utils;

namespace Caelix.Client
{
    /// <summary>
    /// The client's copy of one server entity: the replicated slots of its sectors, its last
    /// received pose and flags, and the scene object that presents it. Renderers and the raycast
    /// read this, never the server.
    /// </summary>
    public sealed class EntityView
    {
        public Guid128 Guid { get; }
        public ushort WorldId { get; }

        /// <summary>
        /// Replica data. Only the replicated slots are populated. The transform is the last
        /// received pose; dirty flags drive the renderer exactly as they do on the server.
        /// </summary>
        public VoxelEntityData Data;

        /// <summary>The scene component presenting this view. Set when bound.</summary>
        public VoxelEntity Component { get; internal set; }

        public bool HasBody { get; internal set; }
        public bool IsStatic => Data.isStatic;
        public bool IsProtected => Data.isProtected;

        /// <summary>True when the client created the GameObject and must destroy it on despawn.</summary>
        public bool IsClientSpawned { get; internal set; }

        /// <summary>
        /// Set for the frame an entity flips to static (including the initial flip on a
        /// born-static body). Every sector renderer of the view consumes it; the renderer clears
        /// it after its per-view loop.
        /// </summary>
        public bool ShouldResetMotionVectors;

        /// <summary>
        /// True once a brick batch has been handed to the applier for this view in the current
        /// client frame. It is the client's only source of brick dirt, so the render propagation
        /// skips every view that has none. Cleared by <see cref="ClientWorld.EndFrame"/>.
        /// </summary>
        internal bool BricksAppliedThisFrame;

        public Transform Transform => Component != null ? Component.transform : null;

        public string Name => Component != null ? Component.name : Guid.ToString();

        public Matrix4x4 LocalToWorld => float4x4.TRS(Data.transform.pos, Data.transform.rot, 1f);

        internal EntityView(Guid128 guid, VoxelEntityData data, ushort worldId)
        {
            Guid = guid;
            WorldId = worldId;
            Data = data;
        }
    }
}
