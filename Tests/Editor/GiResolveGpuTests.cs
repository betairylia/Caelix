using System;
using System.Runtime.InteropServices;
using Caelix.Rendering.GiPrototypes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Caelix.Tests
{
    public class GiResolveGpuTests
    {
        private const string Root = "Packages/ink.irylia.caelix/Runtime/Rendering/Shaders/GiPrototypes/";

        [StructLayout(LayoutKind.Sequential)]
        private struct Surface
        {
            public Vector3 position;
            public uint material;
            public Vector3 normal;
            public uint flags;
            public Vector3 previousPosition;
            public float depth;
            public uint token, face, reserved0, reserved1;
        }

        [SetUp]
        public void RequireGpuReadback()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute and GPU readback are required.");
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                Assert.Ignore("Float32 color targets are unavailable.");
        }

        private static Texture2D Texture(Color[] colors)
        {
            var texture = new Texture2D(colors.Length, 1, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels(colors);
            texture.Apply();
            return texture;
        }

        private static Color[] Read(RenderTexture texture)
        {
            var request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False);
            return request.GetData<Color>().ToArray();
        }

        private static void AssertColor(Color actual, Color expected)
        {
            for (int channel = 0; channel < 4; ++channel)
            {
                Assert.That(float.IsNaN(actual[channel]) || float.IsInfinity(actual[channel]), Is.False);
                float tolerance = Mathf.Max(0.0001f, Mathf.Abs(expected[channel]) * 0.00001f);
                Assert.That(actual[channel], Is.EqualTo(expected[channel]).Within(tolerance), $"channel {channel}");
            }
        }

        [TestCase(true, 4)]
        [TestCase(false, 4)]
        [TestCase(true, 1)]
        public void ResolveAccumulatesStationaryPixelSamplesAndPreservesFloat32Hdr(bool valid, int limit)
        {
            const int count = 8;
            using var state = new CaelixGiResources(count, 1, new CaelixGiSettings());
            Assert.That(state.RawColor.rt.format, Is.EqualTo(RenderTextureFormat.ARGBFloat));
            Assert.That(state.PreviousColor.rt.format, Is.EqualTo(RenderTextureFormat.ARGBFloat));
            Assert.That(state.ResolvedColor.rt.format, Is.EqualTo(RenderTextureFormat.ARGBFloat));
            var previous = new Surface[count];
            var current = new Surface[count];
            var raw = new Color[count];
            var history = new Color[count];
            for (int i = 0; i < count; ++i)
            {
                previous[i] = new Surface { flags = 1, material = 42, token = 11, face = 37, normal = Vector3.forward, depth = 4 };
                current[i] = previous[i];
                raw[i] = new Color(6, 8, 10, 1);
                history[i] = new Color(2, 4, 6, 3);
            }
            current[1].face++;
            current[2].token++;
            current[3].material++;
            current[4].flags = 0;
            current[5] = previous[5] = default;
            history[6].a = 100;
            raw[7] = new Color(200000, 100000, 70000, 1);
            history[7] = new Color(100000, 200000, 70000, 3);
            state.Surfaces.SetData(current);
            state.PreviousSurfaces.SetData(previous);
            var rawTexture = Texture(raw);
            var previousTexture = Texture(history);
            var source = AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + "GiResolve.compute");
            Assert.That(source, Is.Not.Null);
            var shader = Object.Instantiate(source);
            try
            {
                Graphics.CopyTexture(rawTexture, state.RawColor.rt);
                Graphics.CopyTexture(previousTexture, state.PreviousColor.rt);
                int kernel = shader.FindKernel("GiResolve");
                shader.SetInts("g_GiResolution", count, 1);
                shader.SetInt("g_GiAccumulationValid", valid ? 1 : 0);
                shader.SetInt("g_GiAccumulationLimit", limit);
                shader.SetBuffer(kernel, "g_GiSurfaces", state.Surfaces);
                shader.SetBuffer(kernel, "g_GiPreviousSurfaces", state.PreviousSurfaces);
                shader.SetTexture(kernel, "g_GiRawColor", state.RawColor.rt);
                shader.SetTexture(kernel, "g_GiPreviousColor", state.PreviousColor.rt);
                shader.SetTexture(kernel, "g_GiResolvedColor", state.ResolvedColor.rt);
                shader.Dispatch(kernel, 1, 1, 1);
                Color[] result = Read(state.ResolvedColor.rt);
                for (int i = 0; i < count; ++i)
                {
                    // Jitter may sample another face, material, or sky inside this same static pixel.
                    float weight = valid ? Mathf.Min(history[i].a, limit - 1) : 0;
                    Color expected = (raw[i] + history[i] * weight) / (weight + 1);
                    expected.a = weight + 1;
                    AssertColor(result[i], expected);
                }
                Assert.That(result[7].r, Is.GreaterThan(65504), "HDR must survive above the Float16 range");
            }
            finally
            {
                Object.DestroyImmediate(shader);
                Object.DestroyImmediate(rawTexture);
                Object.DestroyImmediate(previousTexture);
            }
        }

        [Test]
        public void PresentShaderCompilesAndDrawsFloat32Hdr()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "GiPresent.shader");
            Assert.That(shader, Is.Not.Null);
            Assert.That(shader.isSupported, Is.True);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            Color expected = new(100000, 70000, 80000, 1);
            var source = Texture(new[] { expected });
            var depth = Texture(new[] { new Color(4, 4, 4, 1) });
            var destination = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            destination.Create();
            var material = new Material(shader);
            try
            {
                var properties = new MaterialPropertyBlock();
                properties.SetTexture("_BlitTexture", source);
                properties.SetTexture("_GiDepth", depth);
                properties.SetVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
                Vector4 previousZBuffer = Shader.GetGlobalVector("_ZBufferParams");
                using var cmd = new CommandBuffer();
                cmd.SetRenderTarget(destination);
                cmd.ClearRenderTarget(false, true, Color.black);
                cmd.SetGlobalVector("_ZBufferParams", new Vector4(0, 0, 1, 0));
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, properties);
                cmd.SetGlobalVector("_ZBufferParams", previousZBuffer);
                Graphics.ExecuteCommandBuffer(cmd);
                foreach (Color pixel in Read(destination)) AssertColor(pixel, expected);
                Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(depth);
                destination.Release();
                Object.DestroyImmediate(destination);
            }
        }

        [TestCase(0f)]
        [TestCase(4f)]
        public void PresentWritesFarDepthForMissesAndProjectedDepthForSurfaces(float eyeDepth)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "GiPresent.shader");
            Assert.That(shader, Is.Not.Null);
            var reader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/ink.irylia.caelix/Tests/Editor/GiDepthReadback.compute");
            Assert.That(reader, Is.Not.Null);
            var source = Texture(new[] { Color.white });
            var linearDepth = Texture(new[] { new Color(eyeDepth, 0, 0, 1) });
            var destination = new RenderTexture(1, 1, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            destination.Create();
            var material = new Material(shader);
            using var result = new ComputeBuffer(1, 4);
            try
            {
                float farDepth = SystemInfo.usesReversedZBuffer ? 0 : 1;
                var properties = new MaterialPropertyBlock();
                properties.SetTexture("_BlitTexture", source);
                properties.SetTexture("_GiDepth", linearDepth);
                properties.SetVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
                Vector4 previousZBuffer = Shader.GetGlobalVector("_ZBufferParams");
                using var cmd = new CommandBuffer();
                cmd.SetRenderTarget(destination);
                // Unity's clear API takes its logical far value (1); the backend
                // reverses it. Shader SV_Depth and readback use raw device depth.
                cmd.ClearRenderTarget(true, true, Color.black, 1);
                cmd.SetGlobalVector("_ZBufferParams", new Vector4(0, 0, 1, 0));
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, properties);
                cmd.SetGlobalVector("_ZBufferParams", previousZBuffer);
                int kernel = reader.FindKernel("ReadDepth");
                cmd.SetComputeTextureParam(reader, kernel, "SourceDepth", destination, 0, RenderTextureSubElement.Depth);
                cmd.SetComputeBufferParam(reader, kernel, "Results", result);
                cmd.DispatchCompute(reader, kernel, 1, 1, 1);
                Graphics.ExecuteCommandBuffer(cmd);
                var depth = new float[1];
                result.GetData(depth);
                AssertColor(Read(destination)[0], Color.white);
                Assert.That(depth[0], Is.EqualTo(eyeDepth <= 0 ? farDepth : 1 / eyeDepth).Within(0.00001f));
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(linearDepth);
                destination.Release();
                Object.DestroyImmediate(destination);
            }
        }
    }
}
