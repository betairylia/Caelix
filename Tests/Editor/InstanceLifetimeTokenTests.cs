using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using Caelix.Rendering.RayQuery;

namespace Caelix.Tests
{
    /// <summary>
    /// The lifetime token of <see cref="CaelixRayQueryInstanceTable"/> slots, which the GI resampler
    /// uses to reject samples whose instance slot was freed and reused.
    /// </summary>
    public class InstanceLifetimeTokenTests
    {
        private CaelixRayQueryInstanceTable table;

        [SetUp]
        public void SetUp()
        {
            table = new CaelixRayQueryInstanceTable();
        }

        [TearDown]
        public void TearDown()
        {
            table.Dispose();
        }

        [Test]
        public void Allocate_AssignsNonZeroToken()
        {
            int slot = table.Allocate();

            Assert.AreNotEqual(0u, table.GetLifetimeToken(slot));
        }

        [Test]
        public void Allocate_TwoSlots_HaveDifferentTokens()
        {
            int a = table.Allocate();
            int b = table.Allocate();

            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(table.GetLifetimeToken(a), table.GetLifetimeToken(b));
        }

        [Test]
        public void Set_PreservesToken()
        {
            int slot = table.Allocate();
            uint token = table.GetLifetimeToken(slot);

            Matrix4x4 prev = Matrix4x4.Translate(new Vector3(1.0f, 2.0f, 3.0f));
            Matrix4x4 current = Matrix4x4.TRS(new Vector3(4.0f, 5.0f, 6.0f), Quaternion.Euler(0.0f, 90.0f, 0.0f), Vector3.one);
            table.Set(slot, prev, current, 274, 1, 42u);

            Assert.AreEqual(token, table.GetLifetimeToken(slot));
        }

        [Test]
        public void FreeThenAllocate_SameSlot_GetsNewToken()
        {
            int slot = table.Allocate();
            uint first = table.GetLifetimeToken(slot);

            table.Free(slot);
            Assert.AreEqual(0u, table.GetLifetimeToken(slot), "A freed slot must read as unallocated.");

            int reused = table.Allocate();
            Assert.AreEqual(slot, reused, "The free list should hand the freed slot back out.");
            uint second = table.GetLifetimeToken(reused);

            Assert.AreNotEqual(0u, second);
            Assert.AreNotEqual(first, second);
        }

        [Test]
        public void Stride_MatchesMarshalledStructSize()
        {
            Assert.AreEqual(Marshal.SizeOf<CaelixRayQueryInstance>(), CaelixRayQueryInstance.Stride);
        }
    }
}
