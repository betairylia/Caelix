using System.IO;
using Caelix.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Caelix.Tests
{
    public unsafe class PackedSceneExportTests
    {
        [Test]
        public void PythonCxwFixtureLoadsEveryCanonicalPackedCodeAndMatchingPreviews()
        {
            string path = Path.GetFullPath("Packages/ink.irylia.caelix/Tests/Editor/Fixtures/packed-materials.cxw");
            // Resolve embedded/local packages through Unity's package registry.
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(
                "Packages/ink.irylia.caelix/Tests/Editor/Fixtures/packed-materials.cxw");
            if (package != null) path = Path.Combine(package.resolvedPath, "Tests/Editor/Fixtures/packed-materials.cxw");
            Assert.That(File.Exists(path), Is.True, path);
            using var reader = SingleFileSaveStorage.OpenRead(path);
            Assert.That(reader.EntityCount, Is.EqualTo(1));
            var record = reader.ReadEntityRecord(0);
            Assert.That(record.IsStatic && !record.HasBody && record.Protected, Is.True);
            var entity = new VoxelEntityData(Allocator.Persistent);
            try
            {
                WorldLoader.LoadEntity(reader, 0, in record, ref entity);
                const int count = 32768 + 16384 + 132;
                for (int i = 0; i < count; i++)
                {
                    int expected = i < 32768 ? 0x8000 + i : i < 49152 ? 0x4000 + i - 32768 : i - 49152 + 1;
                    var xyz = new int3(i % 64 - 33, i / 64 % 64 - 33, i / 4096 - 2);
                    Assert.That(entity.GetBlock(xyz).id, Is.EqualTo((ushort)expected), $"voxel {i}");
                }
                if (BlockEncoding.PackedSceneColor)
                {
                    foreach (var entry in reader.ReadSectorIndex(0))
                    {
                        uint[] expected = reader.ReadPreview(0, entry.Coord);
                        int bricksInRegion = VoxelRegion.SizeInBricks *
                                             VoxelRegion.SizeInBricks * VoxelRegion.SizeInBricks;
                        var actual = new uint[bricksInRegion];
                        Assert.That(PreviewBuilder.Build(in entity, entry.Coord, actual), Is.True);
                        CollectionAssert.AreEqual(expected, actual);
                    }
                }
            }
            finally { entity.Dispose(); }
        }
    }
}
