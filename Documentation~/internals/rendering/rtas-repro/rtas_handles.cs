string outPath = @"C:\Users\betairya\AppData\Local\Temp\claude\F--RP-Games-Caelix-family\7efbb8a1-9d20-4dae-8582-c07e82cc674d\scratchpad\rtas_handles_out.txt";

var sb = new System.Text.StringBuilder();
System.Action<string> L = (s) => { sb.AppendLine(s); UnityEngine.Debug.Log("[RTASH] " + s); };

var createdAS = new System.Collections.Generic.List<UnityEngine.Rendering.RayTracingAccelerationStructure>();
var createdBuf = new System.Collections.Generic.List<UnityEngine.GraphicsBuffer>();

try
{
    L("unityVersion=" + UnityEngine.Application.unityVersion);
    L("graphicsDeviceType=" + UnityEngine.SystemInfo.graphicsDeviceType);
    L("supportsRayTracing=" + UnityEngine.SystemInfo.supportsRayTracing);
    L("supportsInlineRayTracing=" + UnityEngine.SystemInfo.supportsInlineRayTracing);

    var scene0 = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
    L("activeScene=" + scene0.name + " isDirtyBEFORE=" + scene0.isDirty);

    // ---- material ----
    UnityEngine.Material mat = null;
    string matPath = "(none)";
    string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Material brick");
    if (guids != null && guids.Length > 0)
    {
        for (int i = 0; i < guids.Length && mat == null; i++)
        {
            string p = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            var m = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(p);
            if (m != null) { mat = m; matPath = p; }
        }
    }
    if (mat == null)
    {
        guids = UnityEditor.AssetDatabase.FindAssets("t:Material");
        for (int i = 0; i < guids.Length && mat == null; i++)
        {
            string p = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            var m = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(p);
            if (m != null) { mat = m; matPath = p; }
        }
    }
    L("material=" + matPath + " name=" + (mat == null ? "NULL" : mat.name) + " shader=" + (mat == null ? "-" : mat.shader.name));

    // ---- helpers ----
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

    System.Action<string, UnityEngine.Rendering.RayTracingAccelerationStructure> tryBuild = (tag, a) =>
    {
        try { a.Build(); L(tag + " Build() OK"); }
        catch (System.Exception e) { L(tag + " Build() THREW: " + e.GetType().FullName + ": " + e.Message); }
    };

    System.Func<UnityEngine.Rendering.RayTracingAccelerationStructure, string> ic = (a) =>
    {
        try { return a.GetInstanceCount().ToString(); } catch (System.Exception e) { return "ERR:" + e.Message; }
    };

    System.Action<string, System.Collections.Generic.List<int>, UnityEngine.Rendering.RayTracingAccelerationStructure> report =
    (tag, live, a) =>
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        var dups = new System.Collections.Generic.List<int>();
        foreach (var h in live) { if (!seen.Add(h)) dups.Add(h); }
        long cnt = -1;
        try { cnt = (long)a.GetInstanceCount(); } catch (System.Exception e) { L(tag + " GetInstanceCount THREW " + e.Message); }
        L(tag + " LIVE=[" + string.Join(",", live) + "] believedLive=" + live.Count +
          " GetInstanceCount=" + cnt +
          " countsMatch=" + (cnt == live.Count) +
          " DUPLICATES=" + (dups.Count == 0 ? "NONE" : string.Join(",", dups)));
    };

    // ================= TEST A : interleaved remove+add =================
    L("");
    L("===== TEST A (interleaved: Remove(k) then Add, per group) =====");
    {
        var A = mkas();
        var buf = mkbuf();
        var cfg = mkcfg(buf);
        var h = new int[8];
        for (int i = 0; i < 8; i++)
        {
            h[i] = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 10f, 0f, 0f)), (uint)i);
            L("A add i=" + i + " -> handle=" + h[i] + " count=" + ic(A));
        }
        L("A initial handles=[" + string.Join(",", h) + "] count=" + ic(A));
        tryBuild("A", A);

        var live = new System.Collections.Generic.List<int>(h);

        A.RemoveInstance(h[3]);
        L("A RemoveInstance(h3=" + h[3] + ") count=" + ic(A));
        int h3n = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(30f, 5f, 0f)), 3u);
        L("A AddInstance(for k=3) -> handle=" + h3n + " count=" + ic(A));
        live[3] = h3n;

        A.RemoveInstance(h[5]);
        L("A RemoveInstance(h5=" + h[5] + ") count=" + ic(A));
        int h5n = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(50f, 5f, 0f)), 5u);
        L("A AddInstance(for k=5) -> handle=" + h5n + " count=" + ic(A));
        live[5] = h5n;

        report("A FINAL", live, A);
    }

    // ================= TEST B : batched removes then batched adds =================
    L("");
    L("===== TEST B (batched: Remove(h3), Remove(h5), Add, Add) =====");
    {
        var A = mkas();
        var buf = mkbuf();
        var cfg = mkcfg(buf);
        var h = new int[8];
        for (int i = 0; i < 8; i++) h[i] = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 10f, 0f, 0f)), (uint)i);
        L("B initial handles=[" + string.Join(",", h) + "] count=" + ic(A));
        tryBuild("B", A);

        var live = new System.Collections.Generic.List<int>(h);
        A.RemoveInstance(h[3]); L("B RemoveInstance(h3=" + h[3] + ") count=" + ic(A));
        A.RemoveInstance(h[5]); L("B RemoveInstance(h5=" + h[5] + ") count=" + ic(A));
        int b1 = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(30f, 5f, 0f)), 3u);
        L("B AddInstance #1 -> handle=" + b1 + " count=" + ic(A));
        int b2 = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(50f, 5f, 0f)), 5u);
        L("B AddInstance #2 -> handle=" + b2 + " count=" + ic(A));
        live[3] = b1; live[5] = b2;
        report("B FINAL", live, A);
    }

    // ================= TEST C : add before remove =================
    L("");
    L("===== TEST C (add-before-remove, per group) =====");
    {
        var A = mkas();
        var buf = mkbuf();
        var cfg = mkcfg(buf);
        var h = new int[8];
        for (int i = 0; i < 8; i++) h[i] = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 10f, 0f, 0f)), (uint)i);
        L("C initial handles=[" + string.Join(",", h) + "] count=" + ic(A));
        tryBuild("C", A);

        var live = new System.Collections.Generic.List<int>(h);
        int c1 = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(30f, 5f, 0f)), 3u);
        L("C AddInstance(new for k=3) -> handle=" + c1 + " count=" + ic(A));
        A.RemoveInstance(h[3]); L("C RemoveInstance(h3=" + h[3] + ") count=" + ic(A));
        live[3] = c1;

        int c2 = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(50f, 5f, 0f)), 5u);
        L("C AddInstance(new for k=5) -> handle=" + c2 + " count=" + ic(A));
        A.RemoveInstance(h[5]); L("C RemoveInstance(h5=" + h[5] + ") count=" + ic(A));
        live[5] = c2;

        report("C FINAL", live, A);
    }

    // ================= TEST D : does the handle depend on the config? =================
    L("");
    L("===== TEST D (two configs on two different buffers) =====");
    {
        var A = mkas();
        var buf1 = mkbuf();
        var buf2 = mkbuf();
        var cfg1 = mkcfg(buf1);
        var cfg2 = mkcfg(buf2);
        var d1 = new int[3];
        for (int i = 0; i < 3; i++)
        {
            d1[i] = A.AddInstance(cfg1, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 10f, 0f, 0f)), (uint)i);
            L("D cfg1 add i=" + i + " -> handle=" + d1[i] + " count=" + ic(A));
        }
        var d2 = new int[3];
        for (int i = 0; i < 3; i++)
        {
            d2[i] = A.AddInstance(cfg2, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 10f, 20f, 0f)), (uint)(100 + i));
            L("D cfg2 add i=" + i + " -> handle=" + d2[i] + " count=" + ic(A));
        }
        var live = new System.Collections.Generic.List<int>();
        live.AddRange(d1); live.AddRange(d2);
        report("D AFTER-ADDS", live, A);
        tryBuild("D", A);

        A.RemoveInstance(d1[1]);
        L("D RemoveInstance(cfg1 idx1 = " + d1[1] + ") count=" + ic(A));
        int dn = A.AddInstance(cfg2, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(99f, 0f, 0f)), 200u);
        L("D AddInstance(cfg2 new) -> handle=" + dn + " count=" + ic(A));
        live.Remove(d1[1]); live.Add(dn);
        report("D FINAL", live, A);
    }

    // ================= TEST E : Build() between every mutation =================
    L("");
    L("===== TEST E (Build() between mutations) =====");
    {
        var A = mkas();
        var buf = mkbuf();
        var cfg = mkcfg(buf);
        var h = new int[8];
        for (int i = 0; i < 8; i++) h[i] = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(i * 10f, 0f, 0f)), (uint)i);
        L("E initial handles=[" + string.Join(",", h) + "] count=" + ic(A));
        tryBuild("E#1", A);

        var live = new System.Collections.Generic.List<int>(h);
        A.RemoveInstance(h[3]); L("E RemoveInstance(h3=" + h[3] + ") count=" + ic(A));
        int e1 = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(30f, 5f, 0f)), 3u);
        L("E AddInstance(for k=3) -> handle=" + e1 + " count=" + ic(A));
        live[3] = e1;
        tryBuild("E#2", A);

        A.RemoveInstance(h[5]); L("E RemoveInstance(h5=" + h[5] + ") count=" + ic(A));
        int e2 = A.AddInstance(cfg, UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(50f, 5f, 0f)), 5u);
        L("E AddInstance(for k=5) -> handle=" + e2 + " count=" + ic(A));
        live[5] = e2;
        tryBuild("E#3", A);

        report("E FINAL", live, A);
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
    System.IO.File.WriteAllText(outPath, sb.ToString());
    UnityEngine.Debug.Log("[RTASH] wrote " + outPath);
}
