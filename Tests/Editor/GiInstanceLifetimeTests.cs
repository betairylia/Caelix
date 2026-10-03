using Caelix.Rendering.RayQuery;
using NUnit.Framework;
using UnityEngine;

namespace Caelix.Tests
{
    public class GiInstanceLifetimeTests
    {
        [SetUp]
        public void RequireStructuredBuffers()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shaders unavailable.");
        }

        [Test]
        public void ReusedSlotGetsNewLifetimeIdentity()
        {
            using var table = new CaelixRayQueryInstanceTable();
            int slot = table.Allocate();
            uint first = table.GetLifetimeToken(slot);
            Assert.That(first, Is.Not.Zero);
            table.Free(slot);
            Assert.That(table.GetLifetimeToken(slot), Is.Zero);
            int replacement = table.Allocate();
            Assert.That(replacement, Is.EqualTo(slot));
            Assert.That(table.GetLifetimeToken(replacement), Is.Not.EqualTo(first));
            Assert.That(table.GetLifetimeToken(replacement), Is.Not.Zero);
        }

        [Test]
        public void MovingAndCompactingInstancePreservesLifetimeIdentity()
        {
            using var table = new CaelixRayQueryInstanceTable();
            int slot = table.Allocate();
            uint lifetime = table.GetLifetimeToken(slot);
            table.Set(slot, Matrix4x4.identity, 0, 0, 17);
            table.Set(slot, Matrix4x4.Translate(new Vector3(4, 5, 6)), 2740, 2, 91);
            Assert.That(table.GetLifetimeToken(slot), Is.EqualTo(lifetime));
            table.Flush();
            var uploaded = new CaelixRayQueryInstance[table.Capacity];
            table.Buffer.GetData(uploaded);
            Assert.That(uploaded[slot].pad1, Is.EqualTo(lifetime));
            Assert.That(uploaded[slot].brickBase, Is.EqualTo(2740));
            Assert.That(uploaded[slot].page, Is.EqualTo(2));
        }

        [Test]
        public void BufferGrowthAndNewTablesDoNotAliasLiveIdentities()
        {
            using var first = new CaelixRayQueryInstanceTable();
            using var second = new CaelixRayQueryInstanceTable();
            int firstSlot = first.Allocate();
            uint firstToken = first.GetLifetimeToken(firstSlot);
            int initialCapacity = first.Capacity;
            for (int i = 0; i < initialCapacity; ++i) first.Allocate();
            Assert.That(first.Capacity, Is.GreaterThan(initialCapacity));
            Assert.That(first.GetLifetimeToken(firstSlot), Is.EqualTo(firstToken));
            int secondSlot = second.Allocate();
            Assert.That(second.GetLifetimeToken(secondSlot), Is.Not.EqualTo(firstToken));
        }
    }
}
