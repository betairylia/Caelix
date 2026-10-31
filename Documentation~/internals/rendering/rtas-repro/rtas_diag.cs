var sb = new System.Text.StringBuilder();

var allBufs = new System.Collections.Generic.List<UnityEngine.GraphicsBuffer>();
var dead = new System.Collections.Generic.HashSet<UnityEngine.GraphicsBuffer>();
var allAS = new System.Collections.Generic.List<UnityEngine.Rendering.RayTracingAccelerationStructure>();

var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(
    "Packages/ink.irylia.caelix/Runtime/Resources/Caelix_AabbInstance.mat");
if (mat == null) return "MATERIAL NOT FOUND";

System.Func<int, UnityEngine.GraphicsBuffer> MakeBuf = null;
MakeBuf = (n) =>
{
    var b = new UnityEngine.GraphicsBuffer(UnityEngine.GraphicsBuffer.Target.Structured, 4096, 24);
    var arr = new UnityEngine.Bounds[n];
    for (int i = 0; i < n; i++)
        arr[i] = new UnityEngine.Bounds(new UnityEngine.Vector3(i, 0f, 0f), new UnityEngine.Vector3(i + 1, 1f, 1f));
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

try
{
// ---------------------------------------------------------------- D1
// Does disposing an instance's AABB buffer purge that instance by itself,
// with no RemoveInstance and no Build in between?
{
    sb.AppendLine("================ D1: dispose only, watch GetInstanceCount ================");
    var AS = MakeAS();
    var hs = new int[6];
    for (int i = 0; i < 6; i++)
        hs[i] = AS.AddInstance(MakeCfg(MakeBuf(8), 8, new UnityEngine.MaterialPropertyBlock()),
            UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 128, 0, 0)), (uint)i);
    var BA = MakeBuf(70);
    var BB = MakeBuf(271);
    int hA = AS.AddInstance(MakeCfg(BA, 70, new UnityEngine.MaterialPropertyBlock()),
        UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(6400f, 1024f, 6784f)), 6u);
    int hB = AS.AddInstance(MakeCfg(BB, 271, new UnityEngine.MaterialPropertyBlock()),
        UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(6528f, 1024f, 6784f)), 7u);
    sb.AppendLine("  hA=" + hA + " hB=" + hB + " count=" + AS.GetInstanceCount());
    AS.Build();
    sb.AppendLine("  after Build count=" + AS.GetInstanceCount());
    Kill(BA);
    sb.AppendLine("  after Dispose(BA) count=" + AS.GetInstanceCount());
    Kill(BB);
    sb.AppendLine("  after Dispose(BB) count=" + AS.GetInstanceCount());
    var BC = MakeBuf(9);
    int hC = AS.AddInstance(MakeCfg(BC, 9, new UnityEngine.MaterialPropertyBlock()),
        UnityEngine.Matrix4x4.identity, 99u);
    sb.AppendLine("  AddInstance(C) -> handle=" + hC + " count=" + AS.GetInstanceCount()
        + "   (hA=" + hA + " hB=" + hB + ")");
    sb.AppendLine("  RemoveInstance(hA=" + hA + ") -> count=" + AS.GetInstanceCount());
    AS.RemoveInstance(hA);
    sb.AppendLine("    now count=" + AS.GetInstanceCount());
    AS.Dispose();
}

// ---------------------------------------------------------------- D2
// Same, but no Build() ever ran before the dispose.
{
    sb.AppendLine();
    sb.AppendLine("================ D2: dispose before any Build() ================");
    var AS = MakeAS();
    for (int i = 0; i < 6; i++)
        AS.AddInstance(MakeCfg(MakeBuf(8), 8, new UnityEngine.MaterialPropertyBlock()),
            UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 128, 0, 0)), (uint)i);
    var BA = MakeBuf(70);
    int hA = AS.AddInstance(MakeCfg(BA, 70, new UnityEngine.MaterialPropertyBlock()),
        UnityEngine.Matrix4x4.identity, 6u);
    sb.AppendLine("  hA=" + hA + " count=" + AS.GetInstanceCount() + " (no Build yet)");
    Kill(BA);
    sb.AppendLine("  after Dispose(BA) count=" + AS.GetInstanceCount());
    AS.Dispose();
}

// ---------------------------------------------------------------- D3
// Does the purge only hit the instance whose buffer died?
{
    sb.AppendLine();
    sb.AppendLine("================ D3: dispose ONE of two buffers ================");
    var AS = MakeAS();
    for (int i = 0; i < 6; i++)
        AS.AddInstance(MakeCfg(MakeBuf(8), 8, new UnityEngine.MaterialPropertyBlock()),
            UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 128, 0, 0)), (uint)i);
    var BA = MakeBuf(70);
    var BB = MakeBuf(271);
    int hA = AS.AddInstance(MakeCfg(BA, 70, new UnityEngine.MaterialPropertyBlock()),
        UnityEngine.Matrix4x4.identity, 6u);
    int hB = AS.AddInstance(MakeCfg(BB, 271, new UnityEngine.MaterialPropertyBlock()),
        UnityEngine.Matrix4x4.identity, 7u);
    AS.Build();
    sb.AppendLine("  hA=" + hA + " hB=" + hB + " count=" + AS.GetInstanceCount());
    Kill(BA);
    sb.AppendLine("  after Dispose(BA) count=" + AS.GetInstanceCount());
    var BC = MakeBuf(9);
    int hC = AS.AddInstance(MakeCfg(BC, 9, new UnityEngine.MaterialPropertyBlock()),
        UnityEngine.Matrix4x4.identity, 99u);
    sb.AppendLine("  AddInstance(C) -> handle=" + hC + " count=" + AS.GetInstanceCount());
    AS.Dispose();
}
}
catch (System.Exception ex)
{
    sb.AppendLine("EXCEPTION: " + ex.ToString());
}
finally
{
    foreach (var b in allBufs) { if (b != null && dead.Add(b)) b.Dispose(); }
    allBufs.Clear(); dead.Clear();
    foreach (var a in allAS) { try { a.Dispose(); } catch { } }
    allAS.Clear();
}

sb.AppendLine();
sb.AppendLine("scene dirty at end = " + UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty);
return sb.ToString();
