using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Caelix.Tests
{
    public class PackedMaterialShaderTests
    {
        [Test]
        public void ShaderDecodesEveryMaterialAndMatchesCpuFaceLayout()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shaders unavailable.");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/ink.irylia.caelix/Tests/Editor/PackedMaterialDecode.compute");
            Assert.That(shader, Is.Not.Null);
            using var buffer = new ComputeBuffer(65536 * 4, 16);
            int kernel = shader.FindKernel("Decode");
            shader.SetBuffer(kernel, "Results", buffer);
            shader.Dispatch(kernel, 1024, 1, 1);
            var result = new Vector4[65536 * 4];
            buffer.GetData(result);
            for (uint code = 0; code < 65536; code++)
            {
                Vector4 color = result[code * 4], emission = result[code * 4 + 1];
                Vector4 flags = result[code * 4 + 2];
                Assert.That(flags.x, Is.EqualTo(BlockEncoding.IsOpaque(code) ? 1f : 0f), $"opaque {code:X4}");
                Assert.That(flags.y, Is.EqualTo((float)BlockEncoding.Faces(code)), $"faces {code:X4}");
                Assert.That(flags.z, Is.EqualTo((float)BlockEncoding.TransparentId(code)), $"ID {code:X4}");
                if ((code & 0xc000) == 0)
                {
                    var glass = SharedGlassPalette.Get(code & 255);
                    Assert.That(color.x, Is.EqualTo(glass.Tint.x).Within(0.00001));
                    Assert.That(color.y, Is.EqualTo(glass.Tint.y).Within(0.00001));
                    Assert.That(color.z, Is.EqualTo(glass.Tint.z).Within(0.00001));
                    Assert.That(color.w, Is.EqualTo(glass.Ior).Within(0.00001));
                    Assert.That(emission.w, Is.EqualTo(glass.Extinction).Within(0.00001));
                    Assert.That(flags.w, Is.EqualTo(glass.Smoothness).Within(0.00001));
                }
                else
                {
                    bool opaque = (code & 0x8000) != 0;
                    float r = opaque ? ((code >> 10) & 31) / 31f : ((code >> 10) & 15) / 15f;
                    float g = opaque ? ((code >> 5) & 31) / 31f : ((code >> 6) & 15) / 15f;
                    float b = opaque ? (code & 31) / 31f : ((code >> 2) & 15) / 15f;
                    Assert.That(color.x, Is.EqualTo(r).Within(0.00001));
                    Assert.That(color.y, Is.EqualTo(g).Within(0.00001));
                    Assert.That(color.z, Is.EqualTo(b).Within(0.00001));
                    float strength = opaque ? 0 : PackedSceneColor.EmissionStrength(code);
                    Assert.That(emission.x, Is.EqualTo(r * strength).Within(0.0001));
                    Assert.That(emission.y, Is.EqualTo(g * strength).Within(0.0001));
                    Assert.That(emission.z, Is.EqualTo(b * strength).Within(0.0001));
                }
            }
        }
    }
}
