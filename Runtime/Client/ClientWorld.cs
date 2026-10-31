using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Net;
using Caelix.Utils;
using Unity.Jobs;

namespace Caelix.Client
{
    /// <summary>
    /// The client's render replica of one server world. Applies the entity lifecycle messages into
    /// <see cref="EntityView"/> data, binds scene-authored components to their views, spawns view
    /// objects for entities the scene does not have, and runs the Geometry-only dirty propagation
    /// the renderers need. No automata, no physics.
    /// </summary>
    /// <remarks>
    /// Brick residency is not tracked here: a <see cref="NetMessageType.BrickBatch"/> message goes
    /// straight to <see cref="BrickBatchApplier"/> through the view's store, and that decides what
    /// storage to allocate and free. Consumers learn about removals from the change list, in list
    /// order, not from an event. The only thing this class remembers about a batch is that the view
    /// got one, which is what selects the views the render propagation has to visit.
    /// </remarks>
    public sealed class ClientWorld : IDisposable
    {
        private readonly Dictionary<Guid128, EntityView> views = new();
        private readonly List<EntityView> viewList = new();
        private readonly Dictionary<Guid128, VoxelEntity> pendingAuthored = new();
        private bool disposed;

        public ushort Id { get; }
        public bool IsDisposed => disposed;
        public ushort ReplicatedSlotMask { get; internal set; }

        /// <summary>The client that owns this replica.</summary>
        public CaelixClient Owner { get; }

        /// <summary>Views in spawn order. Do not mutate the world while iterating.</summary>
        public IReadOnlyList<EntityView> Views => viewList;

        public event Action<EntityView> ViewSpawned;

        /// <summary>Raised before a view's data is disposed. Renderers release their resources here.</summary>
        public event Action<EntityView> ViewDespawning;

        internal ClientWorld(CaelixClient owner, ushort id, ushort replicatedSlotMask)
        {
            Owner = owner;
            Id = id;
            ReplicatedSlotMask = replicatedSlotMask == 0 ? BrickReplication.DefaultReplicatedSlotMask : replicatedSlotMask;
        }

        public bool TryGetView(Guid128 guid, out EntityView view) => views.TryGetValue(guid, out view);

        #region Authored components

        /// <summary>
        /// A scene component that wants to present the entity with its guid. Bound immediately if
        /// the view exists, otherwise when its spawn message arrives.
        /// </summary>
        public void RegisterAuthored(VoxelEntity component)
        {
            if (component == null) return;
            Guid128 guid = component.PersistentGuid;
            if (guid.IsZero) return;

            if (views.TryGetValue(guid, out EntityView view))
            {
                Bind(view, component, clientSpawned: false);
                return;
            }

            pendingAuthored[guid] = component;
        }

        public void UnregisterAuthored(VoxelEntity component)
        {
            if (component == null) return;
            Guid128 guid = component.PersistentGuid;
            if (pendingAuthored.TryGetValue(guid, out VoxelEntity pending) && pending == component)
            {
                pendingAuthored.Remove(guid);
            }

            if (views.TryGetValue(guid, out EntityView view) && view.Component == component)
            {
                component.UnbindView();
                view.Component = null;
            }
        }

        private void Bind(EntityView view, VoxelEntity component, bool clientSpawned)
        {
            view.Component = component;
            view.IsClientSpawned = clientSpawned;
            component.BindView(view);
            ApplyTransformToComponent(view);
        }

        #endregion

        #region Message application

        internal void OnSpawn(in EntitySpawnMessage message)
        {
            if (views.ContainsKey(message.Guid))
            {
                // A duplicate spawn is a server bug; keep the first.
                Debug.LogWarning($"[ClientWorld {Id}] Duplicate spawn for {message.Guid}; ignored.");
                return;
            }

            var data = new VoxelEntityData(Allocator.Persistent)
            {
                Guid = message.Guid,
                transform = message.Transform,
                previousTransform = message.Transform,
                isStatic = message.IsStatic != 0,
                isProtected = message.IsProtected != 0,
            };

            var view = new EntityView(message.Guid, data, Id)
            {
                HasBody = message.HasBody != 0,
                // A born-static body settles its motion vectors on its first frame.
                ShouldResetMotionVectors = message.IsStatic != 0,
            };
            views.Add(message.Guid, view);
            viewList.Add(view);

            if (pendingAuthored.TryGetValue(message.Guid, out VoxelEntity authored) && authored != null)
            {
                pendingAuthored.Remove(message.Guid);
                Bind(view, authored, clientSpawned: false);
            }
            else
            {
                SpawnViewObject(view);
            }

            ViewSpawned?.Invoke(view);
        }

        private void SpawnViewObject(EntityView view)
        {
            var go = new GameObject($"VoxelEntity_{view.Guid}");
            go.SetActive(false);
            var component = go.AddComponent<VoxelEntity>();
            component.InitializeAsClientView(view.Guid, Owner?.Host);
            go.SetActive(true);
            Bind(view, component, clientSpawned: true);
        }

        internal void OnDespawn(in EntityDespawnMessage message)
        {
            if (!views.TryGetValue(message.Guid, out EntityView view))
            {
                return;
            }

            ViewDespawning?.Invoke(view);

            views.Remove(message.Guid);
            viewList.Remove(view);

            VoxelEntity component = view.Component;
            view.Component = null;
            if (component != null)
            {
                component.UnbindView();
                if (view.IsClientSpawned)
                {
                    DestroyViewObject(component.gameObject);
                }
                else if (component.isActiveAndEnabled)
                {
                    // A replacement with the same GUID must bind back to its authored component.
                    pendingAuthored[message.Guid] = component;
                }
            }

            view.Data.Dispose();
        }

        internal void OnState(in EntityStateMessage message)
        {
            if (!views.TryGetValue(message.Guid, out EntityView view))
            {
                return;
            }

            bool isStatic = message.IsStatic != 0;
            if (!view.Data.isStatic && isStatic)
            {
                view.ShouldResetMotionVectors = true;
            }

            view.Data.isStatic = isStatic;
            view.Data.isProtected = message.IsProtected != 0;
            view.HasBody = message.HasBody != 0;
        }

        internal void OnTransform(in EntityTransformMessage message)
        {
            if (!views.TryGetValue(message.Guid, out EntityView view))
            {
                return;
            }

            view.Data.previousTransform = view.Data.transform;
            view.Data.transform = message.Transform;
            ApplyTransformToComponent(view);
        }

        private static void ApplyTransformToComponent(EntityView view)
        {
            Transform t = view.Transform;
            if (t == null) return;
            RigidTransform pose = view.Data.transform;
            t.SetPositionAndRotation(pose.pos, pose.rot);
            // The pose came from the server; a later hasChanged means someone else moved it.
            t.hasChanged = false;
        }

        #endregion

        private static void DestroyViewObject(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }

        #region Frame

        /// <summary>Clears last frame's require-update flags. Call before applying messages.</summary>
        public void ClearRequireUpdate()
        {
            for (int i = 0; i < viewList.Count; i++)
            {
                EntityView view = viewList[i];
                view.Data.ClearRequireUpdates();
            }
        }

        /// <summary>
        /// Converts this frame's applied bricks into require-update flags for the renderers.
        /// Only Geometry bits propagate, so no storage is ever allocated here.
        /// </summary>
        public void PropagateForRender()
        {
            const BrickUpdateFlags renderFlags =
                BrickUpdateFlags.Geometry | BrickUpdateFlags.GeometryWithLocalNeighbor |
                BrickUpdateFlags.BlockBrickAdded | BrickUpdateFlags.BlockBrickRemoved;

            JobHandle dirtyPropagationHandle = default;

            for (int i = 0; i < viewList.Count; i++)
            {
                EntityView view = viewList[i];
                if (view.Data.entityDirtyFlags == 0 && !view.BricksAppliedThisFrame)
                {
                    continue;
                }

                dirtyPropagationHandle = JobHandle.CombineDependencies(
                    dirtyPropagationHandle,
                    view.Data.PropagateDirtyFlags(renderFlags, async: true)
                );
            }
            
            dirtyPropagationHandle.Complete();

            // Propagation is final here, so this is where the frame's change list is published.
            // Every view, not only the dirty ones: a clean entity's build walks no storage and the
            // guard that catches a double build has to see every view exactly once.
            for (int i = 0; i < viewList.Count; i++)
            {
                viewList[i].Data.BuildChangeList();
            }
        }

        /// <summary>Ends the dirty lifetime of this frame's applied bricks. Call after the renderers ran.</summary>
        public void EndFrame()
        {
            for (int i = 0; i < viewList.Count; i++)
            {
                EntityView view = viewList[i];
                view.Data.ClearDirtyFlags();
                view.Data.ClearChanges();
                view.BricksAppliedThisFrame = false;
            }
        }

        /// <summary>
        /// Records that a brick batch was handed to the applier for <paramref name="view"/> this
        /// frame. Applying bricks is the only thing that dirties replica storage, so this is what
        /// <see cref="PropagateForRender"/> selects on instead of peeking into the storage.
        /// </summary>
        internal static void MarkBricksApplied(EntityView view) => view.BricksAppliedThisFrame = true;

        #endregion

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            for (int i = 0; i < viewList.Count; i++)
            {
                EntityView view = viewList[i];
                ViewDespawning?.Invoke(view);
                VoxelEntity component = view.Component;
                view.Component = null;
                if (component != null)
                {
                    component.UnbindView();
                    if (view.IsClientSpawned && component.gameObject != null)
                    {
                        DestroyViewObject(component.gameObject);
                    }
                }

                view.Data.Dispose();
            }

            viewList.Clear();
            views.Clear();
            pendingAuthored.Clear();
        }
    }

}
