using System;
using System.Collections.Generic;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using Unity.Profiling;
using Caelix.Client;
using Caelix.IO;
using Caelix.Net;
using Caelix.Rendering.Meshing;
using Caelix.Rendering.RayQuery;
using Caelix.Simulation;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Caelix
{
    /// <summary>
    /// Scene bootstrap for the Host role: runs a <see cref="CaelixServer"/> with one world and a
    /// <see cref="CaelixClient"/> in the same process, connected through an in-process channel
    /// with real serialization. There is one host per process; scene-authored
    /// <see cref="VoxelEntity"/> components find it through their own serialized reference, a
    /// parent, or <see cref="Current"/> (see <c>SERVER_CLIENT_ARCHITECTURE.md</c> section 4).
    /// </summary>
    /// <remarks>
    /// Client and Server roles are deferred. This component replaces the former
    /// <c>CaelixWorld</c> MonoBehaviour and keeps its script identity so existing scenes stay wired.
    /// </remarks>
    public class CaelixHost : MonoSingleton<CaelixHost>
    {
        /// <summary>Exclusive CPU timing buckets from the last host frame.</summary>
        public struct HostTimingStats
        {
            public bool IsCreated;
            public bool UsedRayTracing;
            public bool UsedMeshing;
            public int ServerTicks;
            public double ServerMilliseconds;
            public double TickMilliseconds;
            public double PhysicsMilliseconds;
            public double BrickGraphMilliseconds;
            public double ClientMilliseconds;
            public double RenderingMilliseconds;
            public double TotalMilliseconds;
        }

        private const string DefaultSaveLoadFileName = "caelix-world.cxw";

        private static readonly ProfilerMarker s_ServerTickMarker = new("Host.ServerTick");
        private static readonly ProfilerMarker s_ClientFrameMarker = new("Host.ClientFrame");
        private static readonly ProfilerMarker s_RenderersMarker = new("Host.Renderers");

        // ---------------- COMPONENTS ------------------
        [Header("Components")]
        [Tooltip("Scene-authored physics settings for the world. Optional; defaults are used when empty.")]
        [SerializeField] protected PhysicsWorldConfig physicsWorld;

        [SerializeField] protected VoxelRayCast rayCaster;

        [Tooltip("Inline ray query renderer. Enable at most one ray-traced renderer.")]
        [SerializeField] protected CaelixRayQueryRenderer rayQueryRenderer;

        [SerializeField] protected VoxelMeshRendererComponent meshingRenderer;

        // ---------------- SIMULATION ------------------
        [Header("Simulation")]
        [Tooltip("Server ticks per second. The fixed step is 1 / TPS. Ticks run from FixedUpdate, " +
                 "decoupled from the render frame rate.")]
        public float targetTPS = 100.0f;

        [Tooltip("Sets Time.fixedDeltaTime to 1 / Target TPS so FixedUpdate runs one server tick per " +
                 "fixed step. Turn off to keep the project's fixed timestep and tick at that rate instead.")]
        public bool driveFixedTimestep = true;

        /// <summary>External drivers call Step and PumpClientFrame explicitly.</summary>
        public bool ManualDrive { get; set; }

        public long ClientPendingBytes => clientEnd?.PendingBytes ?? 0;
        public long ClientPeakPendingBytes => clientEnd?.PeakPendingBytes ?? 0;
        public long ClientReceivedBytes => clientEnd?.TotalReceivedBytes ?? 0;

        [Header("Alien Dirty Propagation")]
        [Tooltip("Propagate dirtiness between entities through the post-physics brick-overlap graph.")]
        public bool doAlienPropagation = false;

        [Tooltip("Flags a moving (non-static) entity's bricks hand to their alien neighbors.")]
        [SerializeField] private BrickUpdateFlags alienMotionDirtyMask = BrickUpdateFlags.GeneralAutomata;

        [Tooltip("Query every allocated brick of every non-static entity, not only the dirty ones. " +
                 "This is the heaviest input the graph can get; keep it on to benchmark motion.")]
        [SerializeField] private bool alienIncludeMovingBricks = true;

        // ---------------- DEBUG ------------------
        [Header("Debug")]
        [Tooltip("Authoring value for CaelixServer.Frozen; pushed every fixed step. The server still " +
                 "runs its first tick while frozen so the client receives the initial state.")]
        public bool freeze = true;

        // ---------------- SAVE / LOAD ------------------
        [Header("Save / Load")]
        [SerializeField] private bool autoLoadOnStart = false;
        [SerializeField] private string saveLoadPath = DefaultSaveLoadFileName;

        private bool initialized;
        private int ticksSinceLastFrame;
        private long serverTicksElapsed;
        private LocalChannel serverEnd;
        private LocalChannel clientEnd;

        public CaelixServer Server { get; private set; }
        public CaelixClient Client { get; private set; }

        /// <summary>The server world this host simulates.</summary>
        public CaelixWorld World => Server?.DefaultWorld;

        /// <summary>The client replica the renderers and tools read.</summary>
        public ClientWorld ClientWorld => Client?.World;

        public HostTimingStats LastTickTimings { get; private set; }

        /// <summary>Counters of the last alien propagation pass.</summary>
        public BrickOverlapPropagationStats LastBrickOverlapPropagationStats =>
            World != null ? World.LastBrickOverlapPropagationStats : default;

        public BrickOverlapGraph BrickOverlapGraph => World != null ? World.BrickOverlapGraph : default;

        #region Lifecycle

        protected override void OnSingletonEnabled()
        {
            EnsureInitialized();
        }

        /// <summary>
        /// Creates the server, the world, the client, and the channel between them. Safe to call
        /// from any component's <c>OnEnable</c>, which may run before this host's <c>Awake</c>.
        /// </summary>
        public void EnsureInitialized()
        {
            if (!TryClaimSingleton() || initialized)
            {
                return;
            }

            try
            {
                LocalChannel.CreatePair(out serverEnd, out clientEnd);

                Server = new CaelixServer
                {
                    TickRate = targetTPS,
                    Frozen = freeze,
                };
                Server.CreateWorld(BuildWorldConfig());

                Client = new CaelixClient(clientEnd, Server.Types) { Host = this };
                Server.AddConnection(serverEnd);
                initialized = true;
            }
            catch
            {
                DisposeResources();
                enabled = false;
                throw;
            }
            // TODO: VibeReview: Should we abstract the connecting processes etc. similar to Core/Net/INetChannel?
            // Answer (2026-09-05): yes, when the Unity Transport channel lands. The shape is an
            // INetListener (Poll + TryAccept(out INetChannel)) on the server and an INetConnector
            // (Connect(endpoint)) on the client, with a LocalTransport implementing both over the queue
            // pair; CaelixServer.Listen(listener) polls accepts inside ProcessIncoming. Deliberately not
            // built ahead of UTP: the driver update, per-delivery pipelines and connection events should
            // shape the interface, and with LocalChannel alone it would have one implementation and no
            // test of fit.
        }

        private void Start()
        {
            if (autoLoadOnStart)
            {
                Load();
            }
        }

        protected override void OnSingletonDestroyed() => DisposeResources();

        private void DisposeResources()
        {
            initialized = false;
            Client?.Dispose();
            Client = null;
            Server?.Dispose();
            Server = null;
            clientEnd?.Dispose();
            clientEnd = null;
            serverEnd?.Dispose();
            serverEnd = null;
        }

        private CaelixWorldConfig BuildWorldConfig()
        {
            CaelixWorldConfig config = CaelixWorldConfig.Default(0, name);
            if (physicsWorld != null)
            {
                config.physics = physicsWorld.ToSettings();
            }

            config.doAlienPropagation = doAlienPropagation;
            config.alienMotionDirtyMask = alienMotionDirtyMask;
            config.alienIncludeMovingBricks = alienIncludeMovingBricks;
            return config;
        }

        /// <summary>Pushes inspector edits into the running server and world.</summary>
        private void PushSettings()
        {
            if (driveFixedTimestep && targetTPS > 0f)
            {
                float step = 1f / targetTPS;
                if (!Mathf.Approximately(Time.fixedDeltaTime, step))
                {
                    Time.fixedDeltaTime = step;
                }
            }

            // FixedUpdate defines the step; keep the server's own clock in agreement for Step().
            Server.TickRate = Time.fixedDeltaTime > 0f ? 1f / Time.fixedDeltaTime : targetTPS;
            Server.Frozen = freeze;

            CaelixWorld world = World;
            if (world == null) return;
            if (physicsWorld != null)
            {
                world.Config.physics = physicsWorld.ToSettings();
            }

            world.Config.doAlienPropagation = doAlienPropagation;
            world.Config.alienMotionDirtyMask = alienMotionDirtyMask;
            world.Config.alienIncludeMovingBricks = alienIncludeMovingBricks;
        }

        #endregion

        #region Frame

        /// <summary>
        /// One server tick per fixed step. Unity clamps how many fixed steps run after a hitch
        /// (Time.maximumDeltaTime), which is the backlog cap.
        /// </summary>
        private void FixedUpdate()
        {
            if (ManualDrive) return;
            if (!IsCurrent || Server == null || Client == null)
            {
                return;
            }

            PushSettings();
            RunServerTick();
        }

        /// <summary>
        /// One <see cref="CaelixServer.Tick"/>, timed. The server owns the freeze rule, so a frozen
        /// server still runs its first tick here and nothing after it.
        /// </summary>
        private void RunServerTick()
        {
            long start = Stopwatch.GetTimestamp();
            bool ran;
            using (s_ServerTickMarker.Auto()) ran = Server.Tick();
            if (!ran)
            {
                return;
            }

            serverTicksElapsed += Stopwatch.GetTimestamp() - start;
            ticksSinceLastFrame++;
        }

        private void Update()
        {
            if (!ManualDrive) PumpClientFrame();
        }

        /// <summary>Applies queued replication and updates presentation once, without advancing simulation.</summary>
        public void PumpClientFrame()
        {
            if (!IsCurrent || Server == null || Client == null)
            {
                return;
            }

            long frameStart = Stopwatch.GetTimestamp();

            // TODO: VibeReview: Remove this and rely on FixedUpdate one?
            // Queries must also work on frames with no fixed step (including timeScale == 0).
            // This pump leaves every edit command in the channel until Server.Step consumes it.
            Server.ProcessQueries();

            // Get server info by cheating basically, since we are in local hosting mode
            long serverEnd = Stopwatch.GetTimestamp();
            int serverTicks = ticksSinceLastFrame;
            long serverElapsed = serverTicksElapsed;
            ticksSinceLastFrame = 0;
            serverTicksElapsed = 0;

            /////////////////////////////////////////////////////////////////////////
            // Client frame: apply replication, run input, render.
            /////////////////////////////////////////////////////////////////////////

            using (s_ClientFrameMarker.Auto())
            {
                Client.Receive();
                Client.PrepareRender();
                rayCaster?.Tick();
            }

            long clientEnd = Stopwatch.GetTimestamp();

            bool usedRayQuery = rayQueryRenderer != null && rayQueryRenderer.enabled;
            bool usedMeshing = meshingRenderer != null && meshingRenderer.enabled;
            using (s_RenderersMarker.Auto())
            {
                if (usedRayQuery) rayQueryRenderer.Tick();
                if (usedMeshing) meshingRenderer.Tick();
            }

            long renderEnd = Stopwatch.GetTimestamp();

            Client.EndFrame();
            
            /////////////////////////////////////////////////////////////////////////
            // Collect timing metrics
            /////////////////////////////////////////////////////////////////////////
            
            // Server buckets are the LAST tick's split; ServerMilliseconds is the sum of every
            // tick that ran since the previous frame (FixedUpdate may run several, or none).
            TickTimingStats worldTimings = World != null ? World.LastTickTimings : default;
            LastTickTimings = new HostTimingStats
            {
                IsCreated = true,
                UsedRayTracing = usedRayQuery,
                UsedMeshing = usedMeshing,
                ServerTicks = serverTicks,
                ServerMilliseconds = TicksToMilliseconds(serverElapsed),
                TickMilliseconds = serverTicks > 0 ? worldTimings.TickMilliseconds : 0.0,
                PhysicsMilliseconds = serverTicks > 0 ? worldTimings.PhysicsMilliseconds : 0.0,
                BrickGraphMilliseconds = serverTicks > 0 ? worldTimings.BrickGraphMilliseconds : 0.0,
                ClientMilliseconds = TicksToMilliseconds(clientEnd - serverEnd),
                RenderingMilliseconds = TicksToMilliseconds(renderEnd - clientEnd),
                TotalMilliseconds = TicksToMilliseconds(serverElapsed + (Stopwatch.GetTimestamp() - frameStart)),
            };
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>Runs exactly <paramref name="count"/> server ticks now, frozen or not.</summary>
        public void Step(int count = 1)
        {
            EnsureInitialized();
            if (!IsCurrent || !initialized) return;
            PushSettings();
            int steps = Mathf.Max(1, count);
            long start = Stopwatch.GetTimestamp();
            using (s_ServerTickMarker.Auto()) Server.Step(steps);
            serverTicksElapsed += Stopwatch.GetTimestamp() - start;
            ticksSinceLastFrame += steps;
        }

        #endregion

        #region Entities

        /// <summary>
        /// The view components of every replicated entity, in spawn order. Builds a new list on
        /// every call.
        /// </summary>
        public List<VoxelEntity> AllEntities
        {
            get
            {
                var list = new List<VoxelEntity>();
                ClientWorld world = ClientWorld;
                if (world == null) return list;
                IReadOnlyList<EntityView> views = world.Views;
                for (int i = 0; i < views.Count; i++)
                {
                    if (views[i].Component != null) list.Add(views[i].Component);
                }

                return list;
            }
        }

        #endregion

        #region Save / Load

        /// <summary>Saves the server world to a <c>.cxw</c> file at <paramref name="path"/> without a player pose.</summary>
        public void Save(string path)
        {
            Save(path, WorldRecord.None);
        }

        /// <summary>
        /// Saves the server world plus a world-level record (player pose) to a <c>.cxw</c> file at
        /// <paramref name="path"/>. The host does not know the player; the game passes the pose in.
        /// </summary>
        public void Save(string path, in WorldRecord world)
        {
            EnsureInitialized();
            if (!IsCurrent || !initialized)
                throw new InvalidOperationException("Save/load requires an enabled, initialized host that owns the singleton slot.");
            path = EnsureWorldSaveExtension(path);
            World.Save(path, in world);
        }

        [InspectorButton("Save World", PlayModeOnly = true)]
        public void Save()
        {
            string path = ResolveSaveLoadPath();
            Save(path);
            Debug.Log($"Saved Caelix world to {path}", this);
        }

        [InspectorButton("Load World", PlayModeOnly = true)]
        public void Load()
        {
            Load(ResolveSaveLoadPath());
        }

        /// <summary>
        /// Loads every entity stored in the <c>.cxw</c> file at <paramref name="path"/> into the
        /// server world. The client spawns view objects for them on the next tick. Returns the
        /// file's world-level record (player pose) for the game to apply; <see cref="WorldRecord.None"/>
        /// for files older than format v7.
        /// </summary>
        public WorldRecord Load(string path)
        {
            EnsureInitialized();
            if (!IsCurrent || !initialized)
                throw new InvalidOperationException("Save/load requires an enabled, initialized host that owns the singleton slot.");
            path = EnsureWorldSaveExtension(path);
            WorldRecord world = World.Load(path);
            Debug.Log($"Loaded Caelix world from {path}", this);
            return world;
        }

        [InspectorButton("Choose Save/Load Path")]
        private void ChooseSaveLoadPath()
        {
#if UNITY_EDITOR
            string currentPath = ResolveSaveLoadPath();
            string directory = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Application.persistentDataPath;
            }

            string fileName = Path.GetFileName(currentPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = DefaultSaveLoadFileName;
            }

            string selectedPath = EditorUtility.SaveFilePanel(
                "Choose Caelix world file",
                directory,
                fileName,
                "cxw");

            if (string.IsNullOrEmpty(selectedPath))
            {
                return;
            }

            saveLoadPath = selectedPath;
            EditorUtility.SetDirty(this);
#endif
        }

        private string ResolveSaveLoadPath()
        {
            string path = string.IsNullOrWhiteSpace(saveLoadPath)
                ? DefaultSaveLoadFileName
                : saveLoadPath;

            path = EnsureWorldSaveExtension(path);

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(Application.persistentDataPath, path);
        }

        private static string EnsureWorldSaveExtension(string path)
        {
            return string.IsNullOrEmpty(Path.GetExtension(path)) ? path + ".cxw" : path;
        }

        #endregion
    }
}
