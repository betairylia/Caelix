using System;
using Unity.Mathematics;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// One render group = one acceleration-structure instance over a box of bricks. Shifts are per
    /// axis so a group need not be a cube.
    /// </summary>
    /// <remarks>
    /// The three shifts sum to at most <see cref="MaxIndexBits"/>: the brick info word carries the
    /// group-local index in bits 0..15 (coarse occupancy sits at bit 16) and renderer brick ids are
    /// shorts. A value type with no managed fields, because it travels inside Burst jobs.
    /// </remarks>
    public readonly struct RenderGroup : IEquatable<RenderGroup>
    {
        /// <summary>Bits the group-local brick index may use, over all three axes together.</summary>
        public const int MaxIndexBits = 15;

        /// <summary>The default grouping: 16 bricks per axis, the shape milestone 1 shipped.</summary>
        public static readonly RenderGroup Default = new RenderGroup(new int3(4));

        /// <summary>Bits a brick key is shifted right by, per axis, to get its group key.</summary>
        public readonly int3 Shift;

        /// <summary>Builds a grouping from its per-axis shifts.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A component is negative or above <see cref="MaxIndexBits"/>, or the three sum above it.
        /// </exception>
        public RenderGroup(int3 shift)
        {
            if (shift.x < 0 || shift.y < 0 || shift.z < 0
                || shift.x > MaxIndexBits || shift.y > MaxIndexBits || shift.z > MaxIndexBits)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shift), shift.ToString(), $"every shift must be in 0..{MaxIndexBits}.");
            }

            if (shift.x + shift.y + shift.z > MaxIndexBits)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shift), shift.ToString(),
                    $"the three shifts must sum to at most {MaxIndexBits}: the brick info word carries " +
                    "the group-local index in bits 0..15 and renderer brick ids are shorts.");
            }

            Shift = shift;
        }

        /// <summary>Bricks along each axis of a group.</summary>
        public int3 BricksPerAxis => new int3(1 << Shift.x, 1 << Shift.y, 1 << Shift.z);

        /// <summary>Mask that extracts a brick's position inside its group, per axis.</summary>
        public int3 Mask => BricksPerAxis - 1;

        /// <summary>Bits the group-local brick index occupies.</summary>
        public int IndexBits => Shift.x + Shift.y + Shift.z;

        /// <summary>Bricks in one group.</summary>
        public int BricksInGroup => 1 << IndexBits;

        /// <summary>The group a brick key belongs to. Negative keys are handled.</summary>
        public int3 Of(int3 key) => new int3(key.x >> Shift.x, key.y >> Shift.y, key.z >> Shift.z);

        /// <summary>Position of a brick inside its own group.</summary>
        public int3 LocalBrick(int3 key) => key & Mask;

        /// <summary>Flat index of a group-local brick position, x-fastest. Matches the shader's decode.</summary>
        public int LocalBrickIdx(int3 local) => local.x | (local.y << Shift.x) | (local.z << (Shift.x + Shift.y));

        /// <summary>Group-local brick position of a flat index produced by <see cref="LocalBrickIdx"/>.</summary>
        public int3 LocalBrickPos(int idx)
            => new int3(idx & Mask.x, (idx >> Shift.x) & Mask.y, idx >> (Shift.x + Shift.y));

        /// <summary>The lowest brick key of a group.</summary>
        public int3 FirstKey(int3 group)
            => new int3(group.x << Shift.x, group.y << Shift.y, group.z << Shift.z);

        /// <summary>The highest brick key of a group, inclusive.</summary>
        public int3 LastKey(int3 group) => FirstKey(group) + Mask;

        /// <summary>Entity-local block position of a group's first block: the instance's translation.</summary>
        public int3 BlockOrigin(int3 group) => BrickKey.ToBlockOrigin(FirstKey(group));

        public bool Equals(RenderGroup other) => Shift.Equals(other.Shift);

        public override bool Equals(object obj) => obj is RenderGroup other && Equals(other);

        public override int GetHashCode() => Shift.GetHashCode();

        public override string ToString() => $"RenderGroup({Shift.x},{Shift.y},{Shift.z})";
    }

    /// <summary>
    /// The render group shapes a renderer can be set to.
    /// </summary>
    /// <remarks>
    /// Serialized on <see cref="CaelixRayQueryRenderer"/>, so the values are fixed:
    /// <see cref="Bricks16"/> stays 0 and existing components keep the default. Never reorder them.
    /// </remarks>
    public enum RenderGroupSize
    {
        Bricks16 = 0,
        Bricks8 = 1,
        Bricks32 = 2,
        Bricks16x128x16 = 3,
    }

    /// <summary>The grouping each preset stands for, and the shader keyword that decodes it.</summary>
    public static class RenderGroupPresets
    {
        /// <summary>Every non-default keyword, so a dispatch can switch exactly one on.</summary>
        public static readonly string[] Keywords =
        {
            "CAELIX_GROUP_3_3_3", "CAELIX_GROUP_5_5_5", "CAELIX_GROUP_4_7_4"
        };

        /// <summary>The grouping a preset stands for.</summary>
        public static RenderGroup Of(RenderGroupSize size)
        {
            switch (size)
            {
                case RenderGroupSize.Bricks8: return new RenderGroup(new int3(3));
                case RenderGroupSize.Bricks32: return new RenderGroup(new int3(5));
                case RenderGroupSize.Bricks16x128x16: return new RenderGroup(new int3(4, 7, 4));
                default: return RenderGroup.Default;
            }
        }

        /// <summary>Keyword the trace kernels compile for this preset; empty for the default (no keyword).</summary>
        public static string Keyword(RenderGroupSize size)
        {
            switch (size)
            {
                case RenderGroupSize.Bricks8: return Keywords[0];
                case RenderGroupSize.Bricks32: return Keywords[1];
                case RenderGroupSize.Bricks16x128x16: return Keywords[2];
                default: return string.Empty;
            }
        }
    }
}
