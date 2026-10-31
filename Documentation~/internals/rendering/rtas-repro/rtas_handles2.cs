string outPath = @"C:\Users\betairya\AppData\Local\Temp\claude\F--RP-Games-Caelix-family\7efbb8a1-9d20-4dae-8582-c07e82cc674d\scratchpad\rtas_handles_out2.txt";

var sb = new System.Text.StringBuilder();
System.Action<string> L = (s) =>
{
    sb.AppendLine(s);
    UnityEngine.Debug.Log("[RTASH2] " + s);
    try { System.IO.File.WriteAllText(outPath, sb.ToString()); } catch (System.Exception) { }
};

var createdAS = new System.Collections.Generic.List<UnityEngine.Rendering.RayTracingAccelerationStructure>();
var createdBuf = new System.Collections.Generic.List<UnityEngine.GraphicsBuffer>();

try
{
    var scene0 = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
    L("activeScene=" + scene0.name + " isDirtyBEFORE=" + scene0.isDirty);

    UnityEngine.Material mat = null;
    string matPath = "(none)";
    string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Material brick");
    for (int i = 0; guids != null && i < guids.Length && mat == null; i++)
    {
        string p = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
        var m = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(p);
        if (m != null) { mat = m; matPath = p; }
    }
    L("material=" + matPath);

    System.Func<UnityEngine.GraphicsBuffer> mkbuf = () =>
    {
        var b = new UnityEngine.GraphicsBuffer(UnityEngine.GraphicsBuffer.Target.Structured, 16, 24);
        var data = new float[16 * 6];
        for (int i = 0; i < 16; i++)
        {
            data[i * 6 + 0] = i; data[i * 6 + 1] = 0f; data[i * 6 + 2] = 0f;
            data[i * 6 + 3] = i + 1; data[i * 6 + 4] = 1f; data[i * 6 + 5] = 1f;
        }
        b.SetData(data);
        createdBuf.Add(b);
        return b;
    };

    System.Func<UnityEngine.GraphicsBuffer, UnityEngine.Rendering.RayTracingAABBsInstanceConfig> mkcfg = (b) =>
        new UnityEngine.Rendering.RayTracingAABBsInstanceConfig(b, 16, false, mat)
        {
            dynamicGeometry = false,
            accelerationStructureBuildFlagsOverride = true,
            accelerationStructureBuildFlags = UnityEngine.Rendering.RayTracingAccelerationStructureBuildFlags.PreferFastTrace,
        };

    System.Func<UnityEngine.Rendering.RayTracingAccelerationStructure> mkas = () =>
    {
        var s = new UnityEngine.Rendering.RayTracingAccelerationStructure.Settings();
        s.managementMode = UnityEngine.Rendering.RayTracingAccelerationStructure.ManagementMode.Manual;
        s.rayTracingModeMask = UnityEngine.Rendering.RayTracingAccelerationStructure.RayTracingModeMask.Everything;
        s.layerMask = -1;
        var a = new UnityEngine.Rendering.RayTracingAccelerationStructure(s);
        createdAS.Add(a);
        return a;
    };

    System.Func<UnityEngine.Rendering.RayTracingAccelerationStructure, string> ic = (a) =>
    {
        try { return a.GetInstanceCount().ToString(); } catch (System.Exception e) { return "ERR:" + e.Message; }
    };

    System.Func<UnityEngine.Rendering.RayTracingAccelerationStructure, UnityEngine.Rendering.RayTracingAABBsInstanceConfig, int, float, int> add =
        (a, cfg, id, y) =>
        {
            return a.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(id * 10f, y, 0f)), (uint)id);
        };

    // ============ TEST H : does ClearInstances reset the handle space? ============
    L("");
    L("===== TEST H (ClearInstances) =====");
    {
        var A = mkas(); var buf = mkbuf(); var cfg = mkcfg(buf);
        var h = new int[4];
        for (int i = 0; i < 4; i++) h[i] = add(A, cfg, i, 0f);
        L("H handles=[" + string.Join(",", h) + "] count=" + ic(A));
        try { A.ClearInstances(); L("H ClearInstances() OK count=" + ic(A)); }
        catch (System.Exception e) { L("H ClearInstances THREW " + e.GetType().FullName + ": " + e.Message); }
        int n1 = add(A, cfg, 0, 5f); L("H add after clear -> " + n1 + " count=" + ic(A));
        int n2 = add(A, cfg, 1, 5f); L("H add after clear -> " + n2 + " count=" + ic(A));
    }

    // ============ TEST I : free-list order with three removals ============
    L("");
    L("===== TEST I (free-list ordering, 3 removes then 3 adds) =====");
    {
        var A = mkas(); var buf = mkbuf(); var cfg = mkcfg(buf);
        var h = new int[8];
        for (int i = 0; i < 8; i++) h[i] = add(A, cfg, i, 0f);
        L("I handles=[" + string.Join(",", h) + "] count=" + ic(A));
        A.RemoveInstance(h[1]); L("I removed h1=" + h[1] + " count=" + ic(A));
        A.RemoveInstance(h[6]); L("I removed h6=" + h[6] + " count=" + ic(A));
        A.RemoveInstance(h[2]); L("I removed h2=" + h[2] + " count=" + ic(A));
        int i1 = add(A, cfg, 20, 5f); L("I add#1 -> " + i1 + " count=" + ic(A));
        int i2 = add(A, cfg, 21, 5f); L("I add#2 -> " + i2 + " count=" + ic(A));
        int i3 = add(A, cfg, 22, 5f); L("I add#3 -> " + i3 + " count=" + ic(A));
        int i4 = add(A, cfg, 23, 5f); L("I add#4 (free list empty) -> " + i4 + " count=" + ic(A));
    }

    // ============ TEST J : remove the LAST-added handle (high water) ============
    L("");
    L("===== TEST J (remove highest handle, then add) =====");
    {
        var A = mkas(); var buf = mkbuf(); var cfg = mkcfg(buf);
        var h = new int[4];
        for (int i = 0; i < 4; i++) h[i] = add(A, cfg, i, 0f);
        L("J handles=[" + string.Join(",", h) + "] count=" + ic(A));
        A.RemoveInstance(h[3]); L("J removed h3=" + h[3] + " count=" + ic(A));
        int j1 = add(A, cfg, 30, 5f); L("J add -> " + j1 + " count=" + ic(A));
        int j2 = add(A, cfg, 31, 5f); L("J add -> " + j2 + " count=" + ic(A));
    }

    // ============ TEST K : DOUBLE RemoveInstance of the same handle ============
    // Hypothesis for the field observation: a handle freed twice is pushed onto the free list
    // twice, so two later AddInstance calls return the SAME handle.
    L("");
    L("===== TEST K (double RemoveInstance of one handle) =====");
    {
        var A = mkas(); var buf = mkbuf(); var cfg = mkcfg(buf);
        var h = new int[8];
        for (int i = 0; i < 8; i++) h[i] = add(A, cfg, i, 0f);
        L("K handles=[" + string.Join(",", h) + "] count=" + ic(A));
        A.RemoveInstance(h[3]); L("K remove#1 h3=" + h[3] + " count=" + ic(A));
        try { A.RemoveInstance(h[3]); L("K remove#2 h3=" + h[3] + " NO THROW count=" + ic(A)); }
        catch (System.Exception e) { L("K remove#2 THREW " + e.GetType().FullName + ": " + e.Message); }
        int k1 = add(A, cfg, 40, 5f); L("K add#1 -> " + k1 + " count=" + ic(A));
        int k2 = add(A, cfg, 41, 5f); L("K add#2 -> " + k2 + " count=" + ic(A));
        L("K DUPLICATE_AFTER_DOUBLE_REMOVE=" + (k1 == k2));
    }
}
catch (System.Exception ex)
{
    L("FATAL: " + ex.GetType().FullName + ": " + ex.Message);
    L(ex.StackTrace);
}
finally
{
    int nas = 0, nbuf = 0;
    foreach (var a in createdAS) { try { a.Dispose(); nas++; } catch (System.Exception e) { L("dispose AS threw " + e.Message); } }
    foreach (var b in createdBuf) { try { b.Dispose(); nbuf++; } catch (System.Exception e) { L("dispose buf threw " + e.Message); } }
    L("");
    L("disposed AS=" + nas + "/" + createdAS.Count + " buffers=" + nbuf + "/" + createdBuf.Count);
    var scene1 = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
    L("activeScene=" + scene1.name + " isDirtyAFTER=" + scene1.isDirty);
    UnityEngine.Debug.Log("[RTASH2] wrote " + outPath);
}
