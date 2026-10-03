#ifndef CAELIX_GI_RESAMPLING_DATA_INCLUDED
#define CAELIX_GI_RESAMPLING_DATA_INCLUDED

// Structured-buffer layout: 64 bytes. weight is the finalized reservoir weight,
// not the running weight sum. A zero-weight sample still contributes to count.
struct GiReservoir
{
    float3 position;
    uint packedNormal;
    float3 radiance;
    float weight;
    float3 sourcePosition;
    float target;
    uint count;
    uint flags;
    uint frame;
    uint reserved;
};

#define GI_RESERVOIR_VALID 1u
#define GI_RESERVOIR_SKY 2u

// The inspired method retains uniform lit samples and counts the unlit samples
// separately. Each sample contains radiance times its original estimator weight.
// Full world positions avoid a distance-dependent endpoint quantization error.
struct GiCompressedSample
{
    float3 sourcePosition;
    uint packedNormalFlags;
    float3 position;
    uint packedRadiance;
};

// Structured-buffer layout: 16 bytes. Counts can be fractional when history is
// capped; storedCount is the number of independent retained lit samples.
struct GiSampleBucket
{
    float totalCount;
    float litCount;
    uint storedCount;
    uint age;
};

#define GI_BUCKET_WIDTH 8u
#define GI_BUCKET_SAMPLES 8u
#define GI_COMPRESSED_SKY 0x40000000u
#define GI_COMPRESSED_VALID 0x80000000u

float GiRsLuminance(float3 value)
{
    return dot(value, float3(0.2126f, 0.7152f, 0.0722f));
}

float3 GiRsFinite(float3 value)
{
    return any(isnan(value) | isinf(value)) ? 0.0f : max(value, 0.0f);
}

uint GiRsPackRadiance(float3 value)
{
    value = min(GiRsFinite(value), 65408.0f);
    float largest = max(value.x, max(value.y, value.z));
    if (largest < exp2(-24.0f))
        return 0u;
    int exponent = max(-16, (int)floor(log2(largest))) + 1 + 15;
    float scale = exp2(24.0f - exponent);
    if (floor(largest * scale + 0.5f) >= 512.0f)
    {
        exponent++;
        scale *= 0.5f;
    }
    uint3 mantissa = (uint3)min(floor(value * scale + 0.5f), 511.0f);
    return mantissa.x | (mantissa.y << 9) | (mantissa.z << 18) | ((uint)exponent << 27);
}

float3 GiRsUnpackRadiance(uint packed)
{
    return float3(packed & 511u, (packed >> 9) & 511u, (packed >> 18) & 511u)
        * exp2((int)(packed >> 27) - 24.0f);
}

GiCompressedSample GiRsCompress(GiReservoir reservoir)
{
    GiCompressedSample result = (GiCompressedSample)0;
    result.sourcePosition = reservoir.sourcePosition;
    result.position = reservoir.position;
    float3 normal = CaelixUnpackWorldNormal(reservoir.packedNormal);
    uint2 oct = (uint2)round(saturate(PackNormalOctQuadEncode(normal) * 0.5f + 0.5f) * 32767.0f);
    result.packedNormalFlags = oct.x | (oct.y << 15) | GI_COMPRESSED_VALID;
    if ((reservoir.flags & GI_RESERVOIR_SKY) != 0u)
        result.packedNormalFlags |= GI_COMPRESSED_SKY;
    result.packedRadiance = GiRsPackRadiance(reservoir.radiance * reservoir.weight);
    return result;
}

GiReservoir GiRsDecompress(GiCompressedSample sample)
{
    GiReservoir result = (GiReservoir)0;
    result.sourcePosition = sample.sourcePosition;
    result.position = sample.position;
    float2 oct = float2(sample.packedNormalFlags & 32767u, (sample.packedNormalFlags >> 15) & 32767u);
    float3 normal = normalize(UnpackNormalOctQuadEncode(oct * (2.0f / 32767.0f) - 1.0f));
    result.packedNormal = CaelixPackWorldNormal(normal);
    result.radiance = GiRsUnpackRadiance(sample.packedRadiance);
    result.weight = 1.0f;
    result.count = 1u;
    result.flags = (sample.packedNormalFlags & GI_COMPRESSED_VALID) != 0u ? GI_RESERVOIR_VALID : 0u;
    if ((sample.packedNormalFlags & GI_COMPRESSED_SKY) != 0u)
        result.flags |= GI_RESERVOIR_SKY;
    return result;
}

// Solid-angle change of variables when reconnecting the same secondary surface
// point to a different receiver. Sky directions need no change of variables.
float GiRsJacobian(GiReservoir sample, float3 receiver, out float3 direction)
{
    if ((sample.flags & GI_RESERVOIR_SKY) != 0u)
    {
        direction = sample.position;
        return 1.0f;
    }
    float3 oldSegment = sample.position - sample.sourcePosition;
    float3 newSegment = sample.position - receiver;
    float oldDistanceSquared = dot(oldSegment, oldSegment);
    float newDistanceSquared = dot(newSegment, newSegment);
    direction = newSegment * rsqrt(max(newDistanceSquared, 1e-12f));
    float3 normal = CaelixUnpackWorldNormal(sample.packedNormal);
    float oldCosine = dot(normal, -oldSegment * rsqrt(max(oldDistanceSquared, 1e-12f)));
    float newCosine = dot(normal, -direction);
    if (oldDistanceSquared < 1e-10f || newDistanceSquared < 1e-10f || oldCosine <= 1e-7f || newCosine <= 0.0f)
        return 0.0f;
    float jacobian = newCosine * oldDistanceSquared / (oldCosine * newDistanceSquared);
    return isnan(jacobian) || isinf(jacobian) ? 0.0f : jacobian;
}

#endif
