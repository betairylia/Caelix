using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Caelix.Tests
{
    /// <summary>
    /// Pins the acceleration-structure behaviour the renderers' AABB buffer lifetime depends on:
    /// disposing a GraphicsBuffer that a live AABB instance references purges that instance and
    /// frees its handle at once, so a later RemoveInstance with that handle hits whoever inherited
    /// it. Both scene renderers therefore keep the old buffer alive until the instance has been
    /// re-added. If a Unity upgrade changes this, these tests say so before the renderers do.
    /// </summary>
    public class RtasAabbBufferLifetimeTests
    {
        private const string MaterialPath = "Packages/ink.irylia.caelix/Runtime/Resources/Caelix_AabbInstance.mat";

        private sealed class Scope : System.IDisposable
        {
            public readonly RayTracingAccelerationStructure AS;
            public readonly Material Material;
            private readonly List<GraphicsBuffer> buffers = new();

            public Scope()
            {
                var settings = new RayTracingAccelerationStructure.Settings
                {
                    rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
                    managementMode = RayTracingAccelerationStructure.ManagementMode.Manual,
                    layerMask = -1,
                };
                AS = new RayTracingAccelerationStructure(settings);
                Material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
                Assert.That(Material, Is.Not.Null, MaterialPath);
            }

            public GraphicsBuffer Buffer(int boxes)
            {
                var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4096, 24);
                var data = new Vector3[boxes * 2];
                for (int i = 0; i < boxes; i++)
                {
                    data[2 * i] = new Vector3(i, 0, 0);
                    data[2 * i + 1] = new Vector3(i + 1, 1, 1);
                }

                buffer.SetData(data);
                buffers.Add(buffer);
                return buffer;
            }

            public RayTracingAABBsInstanceConfig Config(GraphicsBuffer buffer, int boxes)
                => new RayTracingAABBsInstanceConfig(buffer, boxes, false, Material)
                {
                    dynamicGeometry = false,
                    accelerationStructureBuildFlagsOverride = true,
                    accelerationStructureBuildFlags = RayTracingAccelerationStructureBuildFlags.PreferFastTrace,
                };

            public void Dispose()
            {
                AS.Dispose();
                foreach (GraphicsBuffer buffer in buffers)
                {
                    if (buffer.IsValid()) buffer.Dispose();
                }
            }
        }

        private static void RequireRayTracing()
        {
            if (!SystemInfo.supportsRayTracing)
            {
                Assert.Ignore("This device does not support ray tracing.");
            }
        }

        [Test]
        public void DisposingAReferencedBufferPurgesItsInstance()
        {
            RequireRayTracing();
            using var scope = new Scope();

            GraphicsBuffer a = scope.Buffer(8);
            GraphicsBuffer b = scope.Buffer(8);
            int ha = scope.AS.AddInstance(scope.Config(a, 8), Matrix4x4.identity, 0);
            int hb = scope.AS.AddInstance(scope.Config(b, 8), Matrix4x4.Translate(Vector3.right * 100), 1);
            Assert.That(scope.AS.GetInstanceCount(), Is.EqualTo(2u));
            Assert.That(ha, Is.Not.EqualTo(hb));

            a.Dispose();

            Assert.That(scope.AS.GetInstanceCount(), Is.EqualTo(1u),
                "disposing the buffer of a live instance removes that instance without RemoveInstance");

            // The freed handle is reissued at once, so a stale RemoveInstance would hit the newcomer.
            GraphicsBuffer c = scope.Buffer(8);
            int hc = scope.AS.AddInstance(scope.Config(c, 8), Matrix4x4.Translate(Vector3.up * 100), 2);
            Assert.That(hc, Is.EqualTo(ha), "the purged handle goes back on the free list and is reissued");
        }

        [Test]
        public void ReplacingBuffersBeforeReaddingSharesOneHandle_ReplacingAfterDoesNot()
        {
            RequireRayTracing();

            // The eager order both renderers used: dispose both old buffers, then remove + add each.
            using (var scope = new Scope())
            {
                GraphicsBuffer a = scope.Buffer(70);
                GraphicsBuffer b = scope.Buffer(271);
                int ha = scope.AS.AddInstance(scope.Config(a, 70), Matrix4x4.identity, 0);
                int hb = scope.AS.AddInstance(scope.Config(b, 271), Matrix4x4.Translate(Vector3.right * 128), 1);

                a.Dispose();
                b.Dispose();
                GraphicsBuffer a2 = scope.Buffer(71);
                GraphicsBuffer b2 = scope.Buffer(271);

                scope.AS.RemoveInstance(ha);
                int ha2 = scope.AS.AddInstance(scope.Config(a2, 71), Matrix4x4.identity, 0);
                scope.AS.RemoveInstance(hb);
                int hb2 = scope.AS.AddInstance(scope.Config(b2, 271), Matrix4x4.Translate(Vector3.right * 128), 1);

                Assert.That(ha2, Is.EqualTo(hb2), "the trap: the eager dispose order makes two instances share one handle");
                Assert.That(scope.AS.GetInstanceCount(), Is.EqualTo(1u));
            }

            // The order the renderers use now: remove + add on the new buffer, then dispose the old one.
            using (var scope = new Scope())
            {
                GraphicsBuffer a = scope.Buffer(70);
                GraphicsBuffer b = scope.Buffer(271);
                int ha = scope.AS.AddInstance(scope.Config(a, 70), Matrix4x4.identity, 0);
                int hb = scope.AS.AddInstance(scope.Config(b, 271), Matrix4x4.Translate(Vector3.right * 128), 1);

                GraphicsBuffer a2 = scope.Buffer(71);
                GraphicsBuffer b2 = scope.Buffer(271);

                scope.AS.RemoveInstance(ha);
                int ha2 = scope.AS.AddInstance(scope.Config(a2, 71), Matrix4x4.identity, 0);
                scope.AS.RemoveInstance(hb);
                int hb2 = scope.AS.AddInstance(scope.Config(b2, 271), Matrix4x4.Translate(Vector3.right * 128), 1);
                a.Dispose();
                b.Dispose();

                Assert.That(ha2, Is.Not.EqualTo(hb2));
                Assert.That(scope.AS.GetInstanceCount(), Is.EqualTo(2u));
            }
        }
    }
}
