using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Caelix;
using Caelix.Rendering.Meshing;
using Caelix.Simulation;

/// <summary>
/// Debug GUI overlay for Caelix engine.
/// Displays tick, physics, overlap-graph, and rendering diagnostics, and provides controls
/// for debug visualization.
/// Toggle with P key.
/// </summary>
public class CaelixDebugGUI : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private bool showOnStart = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.P;
    [SerializeField] private KeyCode orbitToggleKey = KeyCode.O;

    [Header("References")]
    [SerializeField] private Caelix.Rendering.RayQuery.CaelixRayQueryRenderer rayQueryRenderer;
    [SerializeField] private VoxelMeshRendererComponent meshRendererComponent;
    [SerializeField] private CaelixHost voxelWorld;

    [Header("Debug Visualization")]
    [SerializeField] private bool showSectorBorders = false;
    [SerializeField] private bool showBrickBorders = false;
    [SerializeField, Min(0), Tooltip(
        "How many regions out from the camera's own region to draw brick borders for. " +
        "0 draws only the region the camera is inside, 1 draws the surrounding 3x3x3, and so on. " +
        "Each region holds 4096 bricks, so this grows cubically - keep it low.")]
    private int brickBorderSectorRadius = 0;
    [SerializeField] private Color sectorBorderColor = new Color(1f, 0f, 0f, 1.0f);
    [SerializeField] private Color brickBorderColor = new Color(0f, 1f, 0f, 1.0f);
    [SerializeField] private Color brickBorderColorDirty = new Color(0f, 1f, 1f, 1.0f);

    /// <summary>Block position to region position, matching VoxelEntity's arithmetic-shift convention.</summary>
    private const int BlockToSectorShift = VoxelRegion.Shift;

    private const float FpsWindowSeconds = 30f;
    private const float PerformanceUpdateIntervalSeconds = 0.1f;
    private const int CurrentSamplesPerPixel = 1;
    private const int FpsGraphWidth = 320;
    private const int FpsGraphHeight = 76;
    private const float SectionSpacing = 6f;
    private const float MinorSpacing = 3f;

    private bool isVisible;
    private GUIStyle boxStyle;
    private GUIStyle buttonStyle;
    private GUIStyle labelStyle;
    private GUIStyle graphLabelStyle;
    private GUIStyle tooltipStyle;
    private bool stylesInitialized = false;

    // Runtime line rendering
    private Material lineMaterial;
    private Texture2D graphTexture;
    private Color[] graphPixels;
    private bool graphTextureDirty = true;

    private readonly Queue<FpsSample> fpsSamples = new Queue<FpsSample>();
    private float currentFps;
    private float currentFrameTimeMs;
    private float currentMraysPerSecond;
    private float fpsEma;
    private float fpsWindowMin;
    private float fpsWindowMax;
    private float performanceSampleElapsed;
    private int performanceSampleFrameCount;
    private float performanceSampleMinFps;
    private float performanceSampleMaxFps;
    private bool hasFpsStats;

    // Rendering mode detection
    private enum RenderingMode
    {
        Unknown,
        RayTraced,
        MeshingFallback,
        Hybrid
    }

    private struct FpsSample
    {
        public readonly float Time;
        public readonly float Fps;
        public readonly float MinFps;
        public readonly float MaxFps;

        public FpsSample(float time, float fps, float minFps, float maxFps)
        {
            Time = time;
            Fps = fps;
            MinFps = minFps;
            MaxFps = maxFps;
        }
    }

    private void Start()
    {
        isVisible = showOnStart;

        // Auto-find references if not set
        if (rayQueryRenderer == null)
        {
            rayQueryRenderer = FindAnyObjectByType<Caelix.Rendering.RayQuery.CaelixRayQueryRenderer>();
        }

        if (voxelWorld == null)
        {
            voxelWorld = FindAnyObjectByType<CaelixHost>();
        }

        meshRendererComponent = FindAnyObjectByType<VoxelMeshRendererComponent>();

        // Create material for runtime line rendering
        CreateLineMaterial();
    }

    private void CreateLineMaterial()
    {
        if (lineMaterial == null)
        {
            // Create a simple unlit shader material for line rendering
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            lineMaterial = new Material(shader);
            lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
        }
    }

    private void Update()
    {
        UpdatePerformanceStats();

        // Don't react to hotkeys while a console/UI is capturing keyboard input.
        if (InputFocus.KeyboardCaptured)
        {
            return;
        }

        // Toggle visibility with P key
        if (Input.GetKeyDown(toggleKey))
        {
            isVisible = !isVisible;
        }
    }

    private void OnDestroy()
    {
        if (lineMaterial != null)
        {
            DestroyImmediate(lineMaterial);
        }

        if (graphTexture != null)
        {
            DestroyImmediate(graphTexture);
        }
    }

    private void InitializeStyles()
    {
        if (stylesInitialized) return;

        // Create semi-transparent background style
        boxStyle = new GUIStyle(GUI.skin.box);
        boxStyle.normal.background = MakeTexture(2, 2, new Color(0f, 0f, 0f, 0.7f));
        boxStyle.padding = new RectOffset(8, 8, 7, 7);
        boxStyle.margin = new RectOffset(3, 3, 3, 3);

        // Create button style
        buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.normal.textColor = Color.white;
        buttonStyle.fontSize = 11;
        buttonStyle.padding = new RectOffset(8, 8, 3, 3);

        // Create label style
        labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.normal.textColor = Color.white;
        labelStyle.fontSize = 11;
        labelStyle.padding = new RectOffset(3, 3, 0, 0);
        labelStyle.richText = true;

        graphLabelStyle = new GUIStyle(labelStyle);
        graphLabelStyle.fontSize = 9;
        graphLabelStyle.padding = new RectOffset(2, 2, 0, 0);

        tooltipStyle = new GUIStyle(labelStyle);
        tooltipStyle.normal.background = MakeTexture(2, 2, new Color(0f, 0f, 0f, 0.9f));
        tooltipStyle.padding = new RectOffset(6, 6, 4, 4);
        tooltipStyle.wordWrap = true;

        stylesInitialized = true;
    }

    private Texture2D MakeTexture(int width, int height, Color color)
    {
        Color[] pixels = new Color[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = color;
        }

        Texture2D texture = new Texture2D(width, height);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
    }

    private void UpdatePerformanceStats()
    {
        float deltaTime = Time.unscaledDeltaTime;
        if (deltaTime <= 0f)
        {
            return;
        }

        float instantFps = 1f / deltaTime;
        performanceSampleElapsed += deltaTime;
        performanceSampleFrameCount++;

        if (performanceSampleFrameCount == 1)
        {
            performanceSampleMinFps = instantFps;
            performanceSampleMaxFps = instantFps;
        }
        else
        {
            performanceSampleMinFps = Mathf.Min(performanceSampleMinFps, instantFps);
            performanceSampleMaxFps = Mathf.Max(performanceSampleMaxFps, instantFps);
        }

        float now = Time.unscaledTime;
        if (!hasFpsStats)
        {
            currentFps = instantFps;
            currentFrameTimeMs = deltaTime * 1000f;
            fpsEma = instantFps;
            fpsWindowMin = instantFps;
            fpsWindowMax = instantFps;
            currentMraysPerSecond = CalculateMraysPerSecond(currentFps);
            hasFpsStats = true;
        }
        else
        {
            float alpha = 1f - Mathf.Exp(-deltaTime / FpsWindowSeconds);
            fpsEma = Mathf.Lerp(fpsEma, instantFps, alpha);
        }

        if (performanceSampleElapsed < PerformanceUpdateIntervalSeconds)
        {
            return;
        }

        currentFps = performanceSampleFrameCount / performanceSampleElapsed;
        currentFrameTimeMs = 1000f / currentFps;
        currentMraysPerSecond = CalculateMraysPerSecond(currentFps);

        fpsSamples.Enqueue(new FpsSample(now, currentFps, performanceSampleMinFps, performanceSampleMaxFps));
        while (fpsSamples.Count > 0 && now - fpsSamples.Peek().Time > FpsWindowSeconds)
        {
            fpsSamples.Dequeue();
        }

        fpsWindowMin = performanceSampleMinFps;
        fpsWindowMax = performanceSampleMaxFps;
        foreach (FpsSample sample in fpsSamples)
        {
            fpsWindowMin = Mathf.Min(fpsWindowMin, sample.MinFps);
            fpsWindowMax = Mathf.Max(fpsWindowMax, sample.MaxFps);
        }

        performanceSampleElapsed = 0f;
        performanceSampleFrameCount = 0;
        graphTextureDirty = true;
    }

    private void OnGUI()
    {
        if (!isVisible) return;

        InitializeStyles();

        // Position in upper-left corner
        GUILayout.BeginArea(new Rect(10, 10, 350, Screen.height - 20));
        GUILayout.BeginVertical(boxStyle);

        // Title
        GUILayout.Label("<b>Caelix Debug Info</b>", labelStyle);
        GUILayout.Space(MinorSpacing);

        // Rendering Mode
        RenderingMode mode = DetectRenderingMode();
        string modeText = mode switch
        {
            RenderingMode.RayTraced => "<color=#00ff00>Ray Traced</color>",
            RenderingMode.MeshingFallback => "<color=#ffaa00>Meshing Fallback</color>",
            RenderingMode.Hybrid => "<color=#00aaff>Hybrid</color>",
            _ => "<color=#ff0000>Unknown</color>"
        };
        GUILayout.Label($"<b>Rendering Mode:</b> {modeText}", labelStyle);

        GUILayout.Space(SectionSpacing);

        DrawTickTimingInfo();

        GUILayout.Space(SectionSpacing);

        // Ray Tracing Info
        if (rayQueryRenderer != null)
        {
            GUILayout.Label("<b>Ray Tracing:</b>", labelStyle);

            Caelix.Simulation.CaelixWorld world = voxelWorld != null ? voxelWorld.World : null;
            if (world != null)
            {
                ulong hostMemory = 0;
                int totalRegions = 0;
                int totalBricks = 0;

                foreach (var entry in world.Entities)
                {
                    VoxelEntityData entity = entry.Value;
                    hostMemory += entity.GetHostMemoryUsageKB();
                    totalRegions += entity.RegionCount;
                    totalBricks += entity.AllocatedBrickCount;
                }

                GUILayout.Label($"  Frame: {rayQueryRenderer.frameId}", labelStyle);
                GUILayout.Label($"  Instances: {rayQueryRenderer.instanceCount}", labelStyle);
                GUILayout.Label($"  Regions: {totalRegions}", labelStyle);
                GUILayout.Label($"  Bricks: {totalBricks}", labelStyle);

                // Memory info
                GUILayout.Label($"  AS Size: {rayQueryRenderer.voxelScene.GetSize() / 1024 / 1024} MB", labelStyle);
                GUILayout.Label($"  Host RAM: {hostMemory / 1024} MB", labelStyle);
                GUILayout.Label(
                    $"  Pool: {rayQueryRenderer.poolLiveBricks} / {rayQueryRenderer.poolCapacityBricks} bricks " +
                    $"in {rayQueryRenderer.poolPages} pages", labelStyle);
            }
        }

        GUILayout.Space(SectionSpacing);

        // Mesh Rendering Info
        if (meshRendererComponent != null && meshRendererComponent.MeshRenderer != null)
        {
            GUILayout.Label("<b>Mesh Rendering:</b>", labelStyle);
            GUILayout.Label($"  Entities: {meshRendererComponent.MeshRenderer.TrackedEntityCount}", labelStyle);
            GUILayout.Label($"  Group Renderers: {meshRendererComponent.MeshRenderer.GroupRendererCount}", labelStyle);
        }

        GUILayout.Space(SectionSpacing);

        DrawBrickOverlapGraphInfo();

        GUILayout.Space(SectionSpacing);

        // Debug Visualization Controls
        GUILayout.Label("<b>Debug Visualization:</b>", labelStyle);

        // Sector borders toggle
        if (GUILayout.Button(new GUIContent(
                showSectorBorders ? "Hide Region Borders" : "Show Region Borders",
                "Toggle world-space outlines for loaded voxel regions."), buttonStyle))
        {
            showSectorBorders = !showSectorBorders;
        }

        // Brick borders toggle
        if (GUILayout.Button(new GUIContent(
                showBrickBorders ? "Hide Brick Borders" : "Show Brick Borders",
                "Toggle brick outlines for the region the camera is inside. Dirty bricks use the " +
                "cyan debug color. Raise Brick Border Sector Radius to include neighbouring regions."), buttonStyle))
        {
            showBrickBorders = !showBrickBorders;
        }

        GUILayout.Space(SectionSpacing);

        // Performance info
        GUILayout.Label("<b>Performance:</b>", labelStyle);
        GUILayout.Label($"  FPS: {currentFps:F1}", labelStyle);
        GUILayout.Label($"  FPS ~30s Avg (EMA): {fpsEma:F1}", labelStyle);
        GUILayout.Label($"  FPS 30s [min,max]: [{fpsWindowMin:F1}, {fpsWindowMax:F1}]", labelStyle);
        GUILayout.Label($"  Frame Time: {currentFrameTimeMs:F2} ms", labelStyle);
        GUILayout.Label($"  Resolution: {Screen.width} x {Screen.height}", labelStyle);
        GUILayout.Label($"  MRays/sec: {currentMraysPerSecond:F2} (SPP {CurrentSamplesPerPixel})", labelStyle);
        DrawFpsGraph();

        GUILayout.Space(MinorSpacing);
        GUILayout.Label($"<i>Press {toggleKey} to toggle GUI, {orbitToggleKey} to toggle orbit</i>", labelStyle);

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private void DrawTickTimingInfo()
    {
        GUILayout.Label("<b>Last Tick CPU Timings:</b>", labelStyle);

        if (voxelWorld == null)
        {
            GUILayout.Label("  Status: <color=#ffaa00>World unavailable</color>", labelStyle);
            return;
        }

        CaelixHost.HostTimingStats timings = voxelWorld.LastTickTimings;
        if (!timings.IsCreated)
        {
            GUILayout.Label("  Status: Waiting for first completed tick", labelStyle);
            return;
        }

        string renderingMode = GetTimedRenderingMode(timings);
        GUILayout.Label($"  Tick (World + Automata): {timings.TickMilliseconds:F3} ms", labelStyle);
        GUILayout.Label($"  Physics: {timings.PhysicsMilliseconds:F3} ms", labelStyle);
        GUILayout.Label($"  Brick Graph: {timings.BrickGraphMilliseconds:F3} ms", labelStyle);
        GUILayout.Label($"  Rendering ({renderingMode}): {timings.RenderingMilliseconds:F3} ms", labelStyle);
        GUILayout.Label($"  Total: {timings.TotalMilliseconds:F3} ms", labelStyle);
    }

    private static string GetTimedRenderingMode(CaelixHost.HostTimingStats timings)
    {
        if (timings.UsedRayTracing && timings.UsedMeshing) return "RT + Meshing";
        if (timings.UsedRayTracing) return "RT";
        if (timings.UsedMeshing) return "Meshing";
        return "Inactive";
    }

    private void DrawBrickOverlapGraphInfo()
    {
        GUILayout.Label("<b>Brick Overlap Graph:</b>", labelStyle);

        if (voxelWorld == null || voxelWorld.World == null)
        {
            GUILayout.Label("  Status: <color=#ffaa00>Physics world unavailable</color>", labelStyle);
            return;
        }

        BrickOverlapGraph graph = voxelWorld.BrickOverlapGraph;
        if (!graph.IsCreated)
        {
            GUILayout.Label("  Status: Waiting for first physics step", labelStyle);
            return;
        }

        BrickOverlapGraphStats stats = voxelWorld.World.Physics.BrickOverlapGraphStats;
        string buildPath = stats.RawCandidates == 0 || stats.NumBodies == 0
            ? "Empty"
            : stats.UsedSerialPath ? "Serial" : "Parallel";

        GUILayout.Label($"  Version: {graph.Version}", labelStyle);
        GUILayout.Label($"  Bodies: {stats.NumBodies}", labelStyle);
        GUILayout.Label($"  Raw Candidates: {stats.RawCandidates}", labelStyle);
        GUILayout.Label($"  Published Pairs: {stats.PublishedPairs}", labelStyle);
        GUILayout.Label($"  Active Source Bricks: {stats.ActiveSourceBricks}", labelStyle);
        GUILayout.Label($"  Build Path: {buildPath}", labelStyle);

        if (voxelWorld != null)
        {
            BrickOverlapPropagationStats propagation = voxelWorld.LastBrickOverlapPropagationStats;
            GUILayout.Label(
                $"  Alien Sources: {propagation.OverlappingSourceBricks} / {propagation.SourceBricks}",
                labelStyle);
            GUILayout.Label($"  Alien Marked Bricks: {propagation.MarkedBricks}", labelStyle);
        }
    }

    private RenderingMode DetectRenderingMode()
    {
        // Match the logic in CaelixWorld.Tick (lines 123-124)
        bool hasRayTracing = rayQueryRenderer != null && rayQueryRenderer.enabled;
        bool hasMesh = meshRendererComponent != null && meshRendererComponent.enabled;

        if (hasRayTracing && hasMesh)
            return RenderingMode.Hybrid;
        else if (hasRayTracing)
            return RenderingMode.RayTraced;
        else if (hasMesh)
            return RenderingMode.MeshingFallback;
        else
            return RenderingMode.Unknown;
    }

    private float CalculateMraysPerSecond(float fps)
    {
        long pixelCount = (long)Screen.width * Screen.height;
        return fps * pixelCount * CurrentSamplesPerPixel / 1000000f;
    }

    private void DrawFpsGraph()
    {
        Rect graphRect = GUILayoutUtility.GetRect(FpsGraphWidth, FpsGraphHeight, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint || fpsSamples.Count == 0)
        {
            return;
        }

        if (graphTexture == null)
        {
            graphTexture = new Texture2D(FpsGraphWidth, FpsGraphHeight, TextureFormat.RGBA32, false);
            graphTexture.hideFlags = HideFlags.HideAndDontSave;
            graphTexture.wrapMode = TextureWrapMode.Clamp;
            graphTexture.filterMode = FilterMode.Bilinear;
            graphPixels = new Color[FpsGraphWidth * FpsGraphHeight];
            graphTextureDirty = true;
        }

        float graphMaxFps = Mathf.Max(30f, Mathf.Ceil(fpsWindowMax / 30f) * 30f);
        if (graphTextureDirty)
        {
            RebuildFpsGraphTexture(graphMaxFps);
            graphTextureDirty = false;
        }

        GUI.DrawTexture(graphRect, graphTexture);

        GUI.Label(new Rect(graphRect.x + 4f, graphRect.y + 2f, graphRect.width - 8f, 16f),
            $"FPS over {FpsWindowSeconds:F0}s", graphLabelStyle);

        TextAnchor previousAlignment = graphLabelStyle.alignment;
        graphLabelStyle.alignment = TextAnchor.UpperRight;
        GUI.Label(new Rect(graphRect.x + 4f, graphRect.y + 2f, graphRect.width - 8f, 16f),
            $"{graphMaxFps:F0}", graphLabelStyle);
        GUI.Label(new Rect(graphRect.x + 4f, graphRect.yMax - 16f, graphRect.width - 8f, 16f),
            "0", graphLabelStyle);
        graphLabelStyle.alignment = previousAlignment;
    }

    private void RebuildFpsGraphTexture(float graphMaxFps)
    {
        Color background = new Color(0.04f, 0.04f, 0.04f, 0.85f);
        Color grid = new Color(1f, 1f, 1f, 0.12f);
        Color line = new Color(0.2f, 0.85f, 1f, 1f);

        for (int i = 0; i < graphPixels.Length; i++)
        {
            graphPixels[i] = background;
        }

        DrawGraphGridLine(0.25f, grid);
        DrawGraphGridLine(0.5f, grid);
        DrawGraphGridLine(0.75f, grid);

        float now = Time.unscaledTime;
        bool hasPrevious = false;
        Vector2Int previous = Vector2Int.zero;

        foreach (FpsSample sample in fpsSamples)
        {
            float age = now - sample.Time;
            int x = FpsGraphWidth - 1 - Mathf.RoundToInt(Mathf.Clamp01(age / FpsWindowSeconds) * (FpsGraphWidth - 1));
            int y = Mathf.RoundToInt(Mathf.Clamp01(sample.Fps / graphMaxFps) * (FpsGraphHeight - 1));
            Vector2Int point = new Vector2Int(x, y);

            if (hasPrevious)
            {
                DrawGraphLine(previous, point, line);
            }

            previous = point;
            hasPrevious = true;
        }

        graphTexture.SetPixels(graphPixels);
        graphTexture.Apply(false);
    }

    private void DrawGraphGridLine(float normalizedHeight, Color color)
    {
        int y = Mathf.RoundToInt(Mathf.Clamp01(normalizedHeight) * (FpsGraphHeight - 1));
        for (int x = 0; x < FpsGraphWidth; x++)
        {
            graphPixels[y * FpsGraphWidth + x] = color;
        }
    }

    private void DrawGraphLine(Vector2Int from, Vector2Int to, Color color)
    {
        int x0 = from.x;
        int y0 = from.y;
        int x1 = to.x;
        int y1 = to.y;
        int dx = Mathf.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;

        while (true)
        {
            DrawGraphPoint(x0, y0, color);
            DrawGraphPoint(x0, y0 - 1, color);

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int error2 = 2 * error;
            if (error2 >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (error2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private void DrawGraphPoint(int x, int y, Color color)
    {
        if (x < 0 || x >= FpsGraphWidth || y < 0 || y >= FpsGraphHeight)
        {
            return;
        }

        graphPixels[y * FpsGraphWidth + x] = color;
    }

    private unsafe void OnRenderObject()
    {
        // if (!isVisible) return;
        if (!showSectorBorders && !showBrickBorders) return;

        Caelix.Client.ClientWorld world = voxelWorld != null ? voxelWorld.ClientWorld : null;
        if (world == null) return;

        // Camera.current is the camera currently rendering, so the scene view and game view each
        // get borders around their own viewpoint rather than sharing one.
        Camera viewCamera = Camera.current != null ? Camera.current : Camera.main;
        bool canDrawBrickBorders = showBrickBorders && viewCamera != null;

        CreateLineMaterial();
        lineMaterial.SetPass(0);

        GL.PushMatrix();
        GL.Begin(GL.LINES);

        // Draw sector and brick borders
        foreach (Caelix.Client.EntityView entity in world.Views)
        {
            if (entity.Transform == null) continue;

            // Get entity's transform matrix (position and rotation; entity scale is always 1)
            Matrix4x4 entityMatrix = entity.LocalToWorld;

            // Which region the camera occupies, in this entity's own local voxel space. Resolved
            // once per entity rather than per region, since each entity has its own transform.
            int3 cameraSectorPos = canDrawBrickBorders
                ? WorldToSectorPos(entity.Transform, viewCamera.transform.position)
                : default;

            if (showSectorBorders)
            {
                NativeArray<int3> regionPositions = entity.Data.GetRegionPositions(Allocator.Temp);
                foreach (int3 regionPos in regionPositions)
                {
                    DrawWireBox(
                        VoxelRegion.OriginOf(regionPos), new float3(VoxelRegion.SizeInBlocks),
                        sectorBorderColor, entityMatrix);
                }

                regionPositions.Dispose();
            }

            // Draw brick borders, but only near the camera: a region holds 4096 bricks, so a
            // large world emits far more lines than the immediate-mode GL path can carry.
            if (canDrawBrickBorders)
            {
                DrawBrickBorders(entity.Data, cameraSectorPos, entityMatrix);
            }
        }

        GL.End();
        GL.PopMatrix();
    }

    /// <summary>
    /// Draws one wire box per brick of the regions around the camera: allocated bricks in the
    /// normal colour, then the bricks that still need geometry work on top, in the dirty colour.
    /// </summary>
    private void DrawBrickBorders(VoxelEntityData data, int3 cameraSectorPos, Matrix4x4 entityMatrix)
    {
        int radius = Mathf.Max(0, brickBorderSectorRadius);
        int3 minRegion = cameraSectorPos - radius;
        int3 maxRegion = cameraSectorPos + radius;
        int3 minKey = VoxelRegion.FirstKeyOf(minRegion);
        int3 maxKey = VoxelRegion.LastKeyOf(maxRegion);

        foreach (int3 key in data.EnumerateBricks(minKey, maxKey))
        {
            DrawWireBox(
                BrickKey.ToBlockOrigin(key), new float3(BrickKey.BlocksPerAxis),
                brickBorderColor, entityMatrix);
        }

        // The dirty ones are collected separately, because a brick position that needs work does
        // not have to hold storage; drawn last so it wins where both apply.
        var dirty = new NativeList<RequiredBrick>(64, Allocator.TempJob); // TempJob: the collector runs a job
        data.CollectRequiredBricks(
            default, BrickUpdateFlags.GeometryWithLocalNeighbor, includeEmpty: true, dirty);
        for (int i = 0; i < dirty.Length; i++)
        {
            int3 key = dirty[i].Key;
            if (math.any(key < minKey) || math.any(key > maxKey))
            {
                continue;
            }

            DrawWireBox(
                BrickKey.ToBlockOrigin(key), new float3(BrickKey.BlocksPerAxis),
                brickBorderColorDirty, entityMatrix);
        }

        dirty.Dispose();
    }

    /// <summary>
    /// Converts a world position into the region coordinate that contains it, in the local voxel
    /// space of <paramref name="entityTransform"/>.
    /// </summary>
    private static int3 WorldToSectorPos(Transform entityTransform, Vector3 worldPosition)
    {
        Vector3 localPosition = entityTransform.InverseTransformPoint(worldPosition);

        // Floor to a block position first, then arithmetic-shift down, exactly as VoxelEntity does.
        // Both steps floor toward negative infinity, which keeps negative coordinates correct.
        return new int3(
            Mathf.FloorToInt(localPosition.x) >> BlockToSectorShift,
            Mathf.FloorToInt(localPosition.y) >> BlockToSectorShift,
            Mathf.FloorToInt(localPosition.z) >> BlockToSectorShift);
    }

    private void DrawWireBox(float3 origin, float3 size, Color color, Matrix4x4 transform)
    {
        GL.Color(color);

        // Define 8 corners in local space
        float3 bottomFrontLeft = origin;
        float3 bottomFrontRight = origin + new float3(size.x, 0, 0);
        float3 bottomBackLeft = origin + new float3(0, 0, size.z);
        float3 bottomBackRight = origin + new float3(size.x, 0, size.z);
        float3 topFrontLeft = origin + new float3(0, size.y, 0);
        float3 topFrontRight = origin + new float3(size.x, size.y, 0);
        float3 topBackLeft = origin + new float3(0, size.y, size.z);
        float3 topBackRight = origin + size;

        // Transform all corners to world space
        Vector3 bfl = transform.MultiplyPoint3x4(bottomFrontLeft);
        Vector3 bfr = transform.MultiplyPoint3x4(bottomFrontRight);
        Vector3 bbl = transform.MultiplyPoint3x4(bottomBackLeft);
        Vector3 bbr = transform.MultiplyPoint3x4(bottomBackRight);
        Vector3 tfl = transform.MultiplyPoint3x4(topFrontLeft);
        Vector3 tfr = transform.MultiplyPoint3x4(topFrontRight);
        Vector3 tbl = transform.MultiplyPoint3x4(topBackLeft);
        Vector3 tbr = transform.MultiplyPoint3x4(topBackRight);

        // Bottom face
        GL.Vertex(bfl); GL.Vertex(bfr);
        GL.Vertex(bfr); GL.Vertex(bbr);
        GL.Vertex(bbr); GL.Vertex(bbl);
        GL.Vertex(bbl); GL.Vertex(bfl);

        // Top face
        GL.Vertex(tfl); GL.Vertex(tfr);
        GL.Vertex(tfr); GL.Vertex(tbr);
        GL.Vertex(tbr); GL.Vertex(tbl);
        GL.Vertex(tbl); GL.Vertex(tfl);

        // Vertical edges
        GL.Vertex(bfl); GL.Vertex(tfl);
        GL.Vertex(bfr); GL.Vertex(tfr);
        GL.Vertex(bbl); GL.Vertex(tbl);
        GL.Vertex(bbr); GL.Vertex(tbr);
    }
}
