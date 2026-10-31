string outPath = @"C:\Users\betairya\AppData\Local\Temp\claude\F--RP-Games-Caelix-family\7efbb8a1-9d20-4dae-8582-c07e82cc674d\scratchpad\rtas_handles_out3.txt";

var sb = new System.Text.StringBuilder();
System.Action<string> L = (s) =>
{
    sb.AppendLine(s);
    UnityEngine.Debug.Log("[RTASH3] " + s);
    try { System.IO.File.WriteAllText(outPath, sb.ToString()); } catch (System.Exception) { }
};

var createdAS = new System.Collections.Generic.List<UnityEngine.Rendering.RayTracingAccelerationStructure>();
var createdBuf = new System.Collections.Generic.List<UnityEngine.GraphicsBuffer>();

try
{
    var scene0 = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
    L("activeScene=" + scene0.name + " isDirtyBEFORE=" + scene0.isDirty);

    UnityEngine.Material mat = null;
    string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Material brick");
    for (int i = 0; guids != null && i < guids.Length && mat == null; i++)
        mat = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]));

    System.Func<UnityEngine.GraphicsBuffer> mkbuf = () =>
    {
        var b = new UnityEngine.GraphicsBuffer(UnityEngine.GraphicsBuffer.Target.Structured, 16, 24);
        var data = new float[16 * 6];
        for (int i = 0; i < 16; i++)
        {
            data[i * 6 + 0] = i; data[i * 6 + 3] = i + 1; data[i * 6 + 4] = 1f; data[i * 6 + 5] = 1f;
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
        (a, cfg, id, y) => a.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(id * 10f, y, 0f)), (uint)id);

    // ===== TEST L : a STALE handle removed after it was already reassigned =====
    L("");
    L("===== TEST L (stale handle removed after reassignment) =====");
    {
        var A = mkas(); var buf = mkbuf(); var cfg = mkcfg(buf);
        int hP = add(A, cfg, 0, 0f);
        int hQ = add(A, cfg, 1, 0f);
        int hR = add(A, cfg, 2, 0f);
        L("L P=" + hP + " Q=" + hQ + " R=" + hR + " count=" + ic(A));
        A.RemoveInstance(hQ); L("L Q removed (handle " + hQ + ") count=" + ic(A));
        int hS = add(A, cfg, 3, 5f); L("L S added -> " + hS + " count=" + ic(A) + "  (S==Q_old: " + (hS == hQ) + ")");
        // Q is buggy and still holds its stale handle. It removes again.
        A.RemoveInstance(hQ); L("L stale Q.RemoveInstance(" + hQ + ") count=" + ic(A) + "  <- this killed S's instance");
        int hT = add(A, cfg, 4, 5f); L("L T added -> " + hT + " count=" + ic(A));
        L("L S and T both believe they own handle " + hS + " : " + (hS == hT));
        L("L FINAL count=" + ic(A) + " liveOwnersBelieved=P,R,S,T=4");
    }

    // ===== TEST M : out-of-range / never-issued handles =====
    L("");
    L("===== TEST M (RemoveInstance with a never-issued handle) =====");
    {
        var A = mkas(); var buf = mkbuf(); var cfg = mkcfg(buf);
        int m1 = add(A, cfg, 0, 0f);
        int m2 = add(A, cfg, 1, 0f);
        int m3 = add(A, cfg, 2, 0f);
        L("M handles=[" + m1 + "," + m2 + "," + m3 + "] count=" + ic(A));
        try { A.RemoveInstance(0); L("M RemoveInstance(0) NO THROW count=" + ic(A)); }
        catch (System.Exception e) { L("M RemoveInstance(0) THREW " + e.GetType().FullName + ": " + e.Message); }
        try { A.RemoveInstance(9999); L("M RemoveInstance(9999) NO THROW count=" + ic(A)); }
        catch (System.Exception e) { L("M RemoveInstance(9999) THREW " + e.GetType().FullName + ": " + e.Message); }
        int m4 = add(A, cfg, 3, 5f); L("M add after bogus removes -> " + m4 + " count=" + ic(A));
        int m5 = add(A, cfg, 4, 5f); L("M add -> " + m5 + " count=" + ic(A));
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
    UnityEngine.Debug.Log("[RTASH3] wrote " + outPath);
}
