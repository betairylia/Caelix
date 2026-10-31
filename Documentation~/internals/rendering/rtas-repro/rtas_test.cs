var sb = new System.Text.StringBuilder();

var allBufs = new System.Collections.Generic.List<UnityEngine.GraphicsBuffer>();
var dead = new System.Collections.Generic.HashSet<UnityEngine.GraphicsBuffer>();
var allAS = new System.Collections.Generic.List<UnityEngine.Rendering.RayTracingAccelerationStructure>();

var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(
    "Packages/ink.irylia.caelix/Runtime/Resources/Caelix_AabbInstance.mat");
if (mat == null) return "MATERIAL NOT FOUND";

// ---- helpers -------------------------------------------------------------

// 4096 x 24-byte structured buffer, first n AABBs filled with min (i,0,0) max (i+1,1,1).
// UnityEngine.Bounds has the raw layout { Vector3 m_Center; Vector3 m_Extents; } = 24 bytes,
// so Bounds(min, max) writes exactly the min/max byte pattern an AABB buffer expects.
System.Func<int, UnityEngine.GraphicsBuffer> MakeBuf = null;
MakeBuf = (n) =>
{
    var b = new UnityEngine.GraphicsBuffer(UnityEngine.GraphicsBuffer.Target.Structured, 4096, 24);
    var arr = new UnityEngine.Bounds[n];
    for (int i = 0; i < n; i++)
    {
        arr[i] = new UnityEngine.Bounds(
            new UnityEngine.Vector3(i, 0f, 0f),
            new UnityEngine.Vector3(i + 1, 1f, 1f));
    }
    b.SetData(arr, 0, 0, n);
    allBufs.Add(b);
    return b;
};

System.Action<UnityEngine.GraphicsBuffer> Kill = null;
Kill = (b) => { if (b != null && dead.Add(b)) b.Dispose(); };

System.Func<UnityEngine.GraphicsBuffer, int, UnityEngine.MaterialPropertyBlock,
    UnityEngine.Rendering.RayTracingAABBsInstanceConfig> MakeCfg = null;
MakeCfg = (b, n, mpb) =>
{
    var c = new UnityEngine.Rendering.RayTracingAABBsInstanceConfig(b, n, false, mat);
    c.dynamicGeometry = false;
    c.accelerationStructureBuildFlagsOverride = true;
    c.accelerationStructureBuildFlags =
        UnityEngine.Rendering.RayTracingAccelerationStructureBuildFlags.PreferFastTrace;
    c.materialProperties = mpb;
    return c;
};

System.Func<UnityEngine.Rendering.RayTracingAccelerationStructure> MakeAS = null;
MakeAS = () =>
{
    var s = new UnityEngine.Rendering.RayTracingAccelerationStructure.Settings();
    s.rayTracingModeMask = UnityEngine.Rendering.RayTracingAccelerationStructure.RayTracingModeMask.Everything;
    s.managementMode = UnityEngine.Rendering.RayTracingAccelerationStructure.ManagementMode.Manual;
    s.layerMask = -1;
    var a = new UnityEngine.Rendering.RayTracingAccelerationStructure(s);
    allAS.Add(a);
    return a;
};

// disposeMode: 0 = per-instance "dispose old, then create new" (renderer pattern)
//              1 = create new, remove+add, THEN dispose old
//              2 = create new, remove+add, never dispose old
//              3 = create both new, dispose both old, then remove+add
// aFirst     : remove+add order
// buildBetween: Build() between the two remove+add pairs
System.Action<string, int, bool, bool> RunTest = null;
RunTest = (name, disposeMode, aFirst, buildBetween) =>
{
    sb.AppendLine();
    sb.AppendLine("================ " + name + " ================");
    sb.AppendLine("disposeMode=" + disposeMode + " aFirst=" + aFirst + " buildBetween=" + buildBetween);

    UnityEngine.Rendering.RayTracingAccelerationStructure AS = null;
    try
    {
        AS = MakeAS();

        // --- step (a): 6 fillers + A + B, then Build() -------------------
        var fillerHandles = new int[6];
        for (int i = 0; i < 6; i++)
        {
            var fb = MakeBuf(8);
            var fmpb = new UnityEngine.MaterialPropertyBlock();
            fmpb.SetFloat("_FillerId", i);
            fillerHandles[i] = AS.AddInstance(MakeCfg(fb, 8, fmpb),
                UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 128, 0, 0)), (uint)i);
            sb.AppendLine("  filler[" + i + "] id=" + i + " handle=" + fillerHandles[i]
                + " count=" + AS.GetInstanceCount());
        }

        var mA = UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(6400f, 1024f, 6784f));
        var mB = UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(6528f, 1024f, 6784f));

        var mpbA = new UnityEngine.MaterialPropertyBlock();
        var mpbB = new UnityEngine.MaterialPropertyBlock();

        var BA = MakeBuf(70);
        mpbA.SetBuffer("g_bricks", BA);
        int hA = AS.AddInstance(MakeCfg(BA, 70, mpbA), mA, 6u);
        AS.UpdateInstancePropertyBlock(hA, mpbA);
        sb.AppendLine("  A  id=6 count=70  handle=" + hA + " instCount=" + AS.GetInstanceCount());

        var BB = MakeBuf(271);
        mpbB.SetBuffer("g_bricks", BB);
        int hB = AS.AddInstance(MakeCfg(BB, 271, mpbB), mB, 7u);
        AS.UpdateInstancePropertyBlock(hB, mpbB);
        sb.AppendLine("  B  id=7 count=271 handle=" + hB + " instCount=" + AS.GetInstanceCount());

        AS.Build();
        sb.AppendLine("  after Build(): instCount=" + AS.GetInstanceCount());

        // --- step (b): buffer swap + remove/add, same frame ---------------
        UnityEngine.GraphicsBuffer BA2 = null;
        UnityEngine.GraphicsBuffer BB2 = null;

        if (disposeMode == 0)
        {
            // renderer's EnsureAABBBuffer(): dispose then allocate, per instance,
            // in the same order the renderer's per-sector loop runs.
            Kill(BA); BA2 = MakeBuf(71);
            Kill(BB); BB2 = MakeBuf(271);
            sb.AppendLine("  [disposed BA -> BA2, disposed BB -> BB2]");
        }
        else if (disposeMode == 3)
        {
            BA2 = MakeBuf(71);
            BB2 = MakeBuf(271);
            Kill(BA); Kill(BB);
            sb.AppendLine("  [created BA2/BB2, then disposed BA/BB]");
        }
        else
        {
            BA2 = MakeBuf(71);
            BB2 = MakeBuf(271);
            sb.AppendLine("  [created BA2/BB2, old buffers still alive]");
        }

        mpbA.SetBuffer("g_bricks", BA2);
        mpbB.SetBuffer("g_bricks", BB2);
        var cfgA2 = MakeCfg(BA2, 71, mpbA);
        var cfgB2 = MakeCfg(BB2, 271, mpbB);

        int hA2 = -1;
        int hB2 = -1;

        System.Action DoA = () =>
        {
            AS.RemoveInstance(hA);
            sb.AppendLine("  RemoveInstance(hA=" + hA + ") -> instCount=" + AS.GetInstanceCount());
            hA2 = AS.AddInstance(cfgA2, mA, 6u);
            sb.AppendLine("  AddInstance(A2, id=6) -> handle=" + hA2 + " instCount=" + AS.GetInstanceCount());
            AS.UpdateInstancePropertyBlock(hA2, mpbA);
        };
        System.Action DoB = () =>
        {
            AS.RemoveInstance(hB);
            sb.AppendLine("  RemoveInstance(hB=" + hB + ") -> instCount=" + AS.GetInstanceCount());
            hB2 = AS.AddInstance(cfgB2, mB, 7u);
            sb.AppendLine("  AddInstance(B2, id=7) -> handle=" + hB2 + " instCount=" + AS.GetInstanceCount());
            AS.UpdateInstancePropertyBlock(hB2, mpbB);
        };

        if (aFirst)
        {
            DoA();
            if (buildBetween) { AS.Build(); sb.AppendLine("  mid Build() -> instCount=" + AS.GetInstanceCount()); }
            DoB();
        }
        else
        {
            DoB();
            if (buildBetween) { AS.Build(); sb.AppendLine("  mid Build() -> instCount=" + AS.GetInstanceCount()); }
            DoA();
        }

        if (disposeMode == 1)
        {
            Kill(BA); Kill(BB);
            sb.AppendLine("  [disposed BA/BB after remove+add]");
        }

        // --- duplicate check ---------------------------------------------
        var live = new System.Collections.Generic.List<string>();
        var vals = new System.Collections.Generic.List<int>();
        for (int i = 0; i < 6; i++) { live.Add("filler" + i); vals.Add(fillerHandles[i]); }
        live.Add("A2"); vals.Add(hA2);
        live.Add("B2"); vals.Add(hB2);

        var dupes = new System.Collections.Generic.List<string>();
        for (int i = 0; i < vals.Count; i++)
        {
            for (int j = i + 1; j < vals.Count; j++)
            {
                if (vals[i] == vals[j]) dupes.Add(live[i] + "==" + live[j] + " (handle " + vals[i] + ")");
            }
        }
        var seq = new System.Collections.Generic.List<string>();
        for (int i = 0; i < vals.Count; i++) seq.Add(live[i] + ":" + vals[i]);
        sb.AppendLine("  LIVE HANDLES: " + string.Join(", ", seq));
        sb.AppendLine("  old handles: A:" + hA + " B:" + hB);
        sb.AppendLine("  DUPLICATE: " + (dupes.Count == 0 ? "none" : string.Join(" | ", dupes)));

        AS.Build();
        sb.AppendLine("  after final Build(): instCount=" + AS.GetInstanceCount());
    }
    catch (System.Exception ex)
    {
        sb.AppendLine("  EXCEPTION: " + ex.ToString());
    }
    finally
    {
        if (AS != null) AS.Dispose();
    }
};

try
{
    RunTest("TEST 2-A  dispose old AFTER remove+add, order A then B", 1, true, false);
    RunTest("TEST 3-A  never dispose old buffers, order A then B", 2, true, false);
    RunTest("TEST 4-A  Build() between the pairs, order A then B", 0, true, true);
    RunTest("TEST 5-A  create new BEFORE disposing old, order A then B", 3, true, false);
}
finally
{
    foreach (var b in allBufs) { if (b != null && dead.Add(b)) b.Dispose(); }
    allBufs.Clear();
    dead.Clear();
    foreach (var a in allAS) { try { a.Dispose(); } catch { } }
    allAS.Clear();
}

sb.AppendLine();
sb.AppendLine("scene dirty at end = " + UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty);
return sb.ToString();
