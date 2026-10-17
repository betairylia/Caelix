using System;
using UnityEngine;
using Caelix.Client;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Caelix.Rendering.Native
{
    /// <summary>
    /// The per-frame tick and the sessions of <see cref="CaelixNativeRenderer"/>: binding the client
    /// world, starting and ending a session, the frame packet of the frame, and the status tripwires.
    /// </summary>
    public partial class CaelixNativeRenderer
    {
        private ClientWorld source;
        private int lastTickFrame = -1;
        private float lastDeferredWarningTime = float.NegativeInfinity;

        private bool sessionLive;
        private EncodingOverride sessionEncodingOverride;
        private uint sessionEncoding;
        private TextAsset sessionMaterialsAsset;
        /// <summary>Material entries of the session (block-ID encoding only), sent with the reset.</summary>
        private CaelixNativeMaterialEntry[] sessionMaterials;
        private int sessionStagingMiB;
        /// <summary>Sequence of the session's reset frame packet.</summary>
        private ulong sessionResetSequence;
        /// <summary>The plugin consumed the reset packet: the status describes this session's scene.</summary>
        private bool sessionReady;
        private ulong sessionDroppedBaseline;
        private bool sessionConfigLogged;
        private bool sessionEncodingLogged;
        private bool sessionStagingLogged;

        private uint ResolveEncoding()
        {
            switch (encodingOverride)
            {
                case EncodingOverride.Packed: return 0u;
                case EncodingOverride.BlockId: return 1u;
                default: return BlockEncoding.PackedSceneColor ? 0u : 1u;
            }
        }

        /// <summary>Binds the host's client world. <paramref name="rebound"/> is true when a world was bound now.</summary>
        private bool EnsureSource(out bool rebound)
        {
            rebound = false;
            if (source != null && !source.IsDisposed) return true;
            if (host == null) host = CaelixHost.Current;
            if (host == null) return false;
            host.EnsureInitialized();
            ClientWorld world = host.ClientWorld;
            if (world == null || world.IsDisposed) return false;
            UnbindSource();
            source = world;
            rebound = true;
            return true;
        }

        private void UnbindSource()
        {
            feed?.Forget();
            source = null;
        }

        /// <summary>
        /// Collects this frame's scene changes and issues the frame event with them; starts a
        /// session first when none is live. Call once per frame after the client's
        /// <c>PrepareRender</c> and before <c>EndFrame</c>: the change lists are read here and
        /// nowhere cleared.
        /// </summary>
        public void Tick()
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                TickInner();
            }
            finally
            {
                LastTickMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                feed?.RecordTick(LastTickMilliseconds);
            }
        }

        private void TickInner()
        {
            if (lastTickFrame == Time.frameCount)
            {
                Debug.LogWarning($"CaelixNativeRenderer: Tick ran twice in frame {Time.frameCount}; the change list is " +
                                 "read once per frame, so the second run sends nothing new.", this);
                return;
            }
            lastTickFrame = Time.frameCount;
            if (!CaelixNativeBridge.IsAvailable) return;
            if (!EnsureSource(out bool rebound)) return;
            if (rebound && sessionLive)
            {
                // A new client world: the session start below releases the old scene.
                if (verboseLogging)
                {
                    Debug.Log("Caelix native: a new client world; the session restarts and the world is sent again.", this);
                }
                sessionLive = false;
            }

            CaelixNativeBridge.TryGetStatus(out CaelixNativeStatus status);
            CaelixNativePacketRings.Maintain(status);
            if (sessionLive && !CheckSession(status)) return;

            bool starting = !sessionLive;
            int frameSlot = rings.FindFreeFrameSlot(status);
            if (starting)
            {
                if (frameSlot < 0 || !rings.HasFreeRelease(status))
                {
                    TicksDeferred++;
                    WarnDeferred("the session start waits for a free packet slot");
                    return;
                }
                BeginSession();
            }

            feed.CollectChanges(source);
            feed.TrackStream(status, rings.BricksInFlight(status), verboseLogging, this);
            if (statusLogInterval > 0 && Time.frameCount % statusLogInterval == 0)
            {
                LogStatus(status);
            }

            frameCommands.Clear();
            bool issue = false;
            IntPtr eventFunc = CaelixNativeBridge.RenderEventFunc;
            if (starting)
            {
                // Destroys the previous scene and every renderer (when there are any); the reset
                // frame packet below creates the new scene.
                IntPtr release = rings.FillRelease(CaelixNativeBridge.ReleaseScene, 0, status);
                frameCommands.IssuePluginEventAndData(eventFunc, status.releaseEventId, release);
                issue = true;
            }

            // Views whose camera is gone or idle: their renderers go (a scene release covers them).
            idleViewScratch.Clear();
            for (int v = 0; v < viewSlots.Length; v++)
            {
                if (!ViewIsIdle(v)) continue;
                if (!starting)
                {
                    IntPtr release = rings.FillRelease(CaelixNativeBridge.ReleaseView, (uint)v, status);
                    // No free release slot: wait for the next tick.
                    if (release == IntPtr.Zero) break;
                    frameCommands.IssuePluginEventAndData(eventFunc, status.releaseEventId, release);
                    issue = true;
                }
                idleViewScratch.Add(v);
            }

            bool frameIssued = false;
            if (frameSlot >= 0)
            {
                var config = new CaelixNativeEntityFeed.FrameConfig
                {
                    Reset = starting,
                    Encoding = sessionEncoding,
                    StagingMiB = (uint)sessionStagingMiB,
                    Materials = sessionMaterials,
                };
                if (feed.FillFramePacket(rings, frameSlot, UploadBudgetBricks(status), config, this, out ulong sequence))
                {
                    if (starting) sessionResetSequence = sequence;
                    frameCommands.IssuePluginEventAndData(eventFunc, status.frameEventId, rings.FramePacket(frameSlot));
                    issue = true;
                    frameIssued = true;
                }
            }
            else
            {
                // Everything collected waits in the entity states for the next frame packet.
                TicksDeferred++;
                WarnDeferred("no frame packet slot is free; the scene records of this frame wait");
            }

            if (issue) Graphics.ExecuteCommandBuffer(frameCommands);
            // After the release events: the plugin drops its views of the targets before Unity destroys them.
            foreach (int v in idleViewScratch) FreeViewSlot(v);
            if (starting && frameIssued)
            {
                sessionLive = true;
                // The scene release also released every target view.
                s_releasedGeneration = s_generation;
                if (verboseLogging)
                {
                    Debug.Log($"Caelix native: session started: encoding {sessionEncoding}, staging ring {sessionStagingMiB} MiB, " +
                              $"{(sessionMaterials != null ? sessionMaterials.Length : 0)} material entries, " +
                              $"{feed.EntityCount} entities, {feed.BricksPending} bricks to send.", this);
                }
            }
        }

        /// <summary>Fixes the session config and forgets every entity: the world is sent again.</summary>
        private void BeginSession()
        {
            sessionEncodingOverride = encodingOverride;
            sessionEncoding = ResolveEncoding();
            sessionMaterialsAsset = materialsJson;
            sessionMaterials = sessionEncoding == 1u ? CaelixNativeMaterialJson.Parse(materialsJson, verboseLogging, this) : null;
            if (sessionEncoding != 1u && materialsJson != null && verboseLogging)
            {
                Debug.Log($"Caelix native: {materialsJson.name} is ignored: the packed encoding has no material table.", this);
            }
            sessionStagingMiB = Mathf.Max(4, stagingRingMiB);
            sessionResetSequence = ulong.MaxValue;
            sessionReady = false;
            sessionDroppedBaseline = 0;
            sessionConfigLogged = false;
            sessionEncodingLogged = false;
            sessionStagingLogged = false;
            feed.Forget();
        }

        /// <summary>
        /// The tripwires of a live session. False when the session ended (the plugin dropped
        /// bricks); the next tick starts a new one.
        /// </summary>
        private bool CheckSession(in CaelixNativeStatus status)
        {
            if (!sessionConfigLogged &&
                (ResolveEncoding() != sessionEncoding || !ReferenceEquals(materialsJson, sessionMaterialsAsset)))
            {
                sessionConfigLogged = true;
                Debug.LogError("Caelix native: the encoding or the material JSON changed during the session; the session " +
                               "keeps its own. Disable the component before changing them.", this);
            }
            if (!sessionStagingLogged && stagingRingMiB != sessionStagingMiB)
            {
                sessionStagingLogged = true;
                if (verboseLogging)
                {
                    Debug.Log($"Caelix native: staging ring {stagingRingMiB} MiB applies at the next session (this one has " +
                              $"{sessionStagingMiB} MiB).", this);
                }
            }

            // Until the plugin consumed the reset packet, the status describes the previous scene.
            if (status.lastConsumedFrameSequence < sessionResetSequence) return true;
            if (!sessionReady)
            {
                // Frame packets of an earlier session were consumed before the reset: their drops
                // are in this baseline, and this session sends no bricks before it is ready.
                sessionReady = true;
                sessionDroppedBaseline = status.bricksDropped;
            }
            if (!sessionEncodingLogged && status.sceneEncoding != CaelixNativeBridge.NoScene &&
                status.sceneEncoding != sessionEncoding)
            {
                sessionEncodingLogged = true;
                Debug.LogError($"Caelix native: the plugin's scene has encoding {status.sceneEncoding}, the session " +
                               $"{sessionEncoding}.", this);
            }
            if (status.bricksDropped > sessionDroppedBaseline)
            {
                Debug.LogError($"Caelix native: the plugin dropped {status.bricksDropped - sessionDroppedBaseline} bricks " +
                               "(its staging ring refused them); the session restarts and the world is sent again.", this);
                sessionLive = false;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Bricks the next frame packet may carry: the staging ring's free upload capacity after the
        /// newest flush, minus the bricks of frame packets issued since, capped at the per-packet
        /// cap. 0 before the plugin created this session's scene.
        /// </summary>
        private long UploadBudgetBricks(in CaelixNativeStatus status)
        {
            if (!sessionReady || status.sceneEncoding == CaelixNativeBridge.NoScene) return 0;
            long free = (long)Math.Min(status.uploadBricksFree, long.MaxValue) - rings.BricksInFlight(status);
            long budget = Math.Min(free, (long)Math.Min(status.uploadBricksCap, long.MaxValue));
            return budget > 0 ? budget : 0;
        }

        private void WarnDeferred(string reason)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastDeferredWarningTime < 5.0f) return;
            lastDeferredWarningTime = now;
            Debug.LogWarning($"Caelix native: {reason} ({TicksDeferred} deferred ticks so far).", this);
        }

        private void LogStatus(in CaelixNativeStatus s)
        {
            Debug.Log($"Caelix native status (frame {Time.frameCount}): frames {s.frames}, renders {s.renders}, failed " +
                      $"{s.failedEvents}, last error {s.lastError}, resets {s.sceneResets}, live views 0x{s.liveViews:X2}, " +
                      $"encoding {s.sceneEncoding}, entities {s.entities}, groups {s.groups}, resident {s.bricksResident}, " +
                      $"pending C# {BricksPendingCSharp}, in flight {rings.BricksInFlight(s)}, free/cap {s.uploadBricksFree}/" +
                      $"{s.uploadBricksCap}, dropped {s.bricksDropped}, as_builds_pending {s.asBuildsPending}, GPU flush " +
                      $"{s.flushMs:F3} ms trace {s.traceMs:F3} ms composite {s.compositeMs:F3} ms denoise {s.denoiseMs:F3} ms " +
                      $"(view {s.timingsView}); Tick {LastTickMilliseconds:F3} ms, deferred {TicksDeferred}, skipped " +
                      $"{PacketsSkipped}; jitter ({LastJitter.x:F3}, {LastJitter.y:F3}) px.", this);
        }
    }
}
