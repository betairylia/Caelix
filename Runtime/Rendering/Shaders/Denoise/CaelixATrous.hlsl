#ifndef CAELIX_ATROUS_INCLUDED
#define CAELIX_ATROUS_INCLUDED

// Edge-aware a-trous wavelet filter (SVGF-lite, no variance texture).
//
// The signal to filter is named by CAELIX_ATROUS_SIGNAL_TEX, which the including shader must
// declare as a TEXTURE2D before including this file. Signal convention, identical for the path
// tracer's radiance and the budget mode's AO/shadow:
//   .rgb = the quantity being filtered   .a = validity (<= 0.001 means "no data, pass through")
//
// Geometry guides (_NormalTex, _CurrentDepthHistoryTex) come from CaelixDenoiseCommon.hlsl.
//
// The depth weight is an exact PLANE PREDICTION, not a depth difference: the centre pixel's normal
// defines a plane, every tap ray is intersected with it, and the tap is compared against the depth
// it would have had ON that plane. A flat surface therefore gets weight 1 in every direction and at
// every angle -- no anisotropic smearing along the depth gradient, no band where a grazing plane
// self-rejects -- and only a genuine depth step (a different surface) is rejected.

#include "CaelixDenoiseCommon.hlsl"

#ifndef CAELIX_ATROUS_SIGNAL_TEX
#define CAELIX_ATROUS_SIGNAL_TEX _IndirectRadianceTex
#endif

int _ATrousStepWidth;
int _ATrousUseFaceHash;
int _ATrousJitterTaps;
int _ATrousFrameIndex;
float _ATrousNormalPower;
float _ATrousDepthTolerance;
float _ATrousRelativeDepthTolerance;
float _ATrousRadianceSigma;

float CaelixATrousKernel(int offset)
{
    offset = abs(offset);
    if (offset == 0)
    {
        return 0.375f;
    }

    if (offset == 1)
    {
        return 0.25f;
    }

    return 0.0625f;
}

float4 CaelixATrousFilter(uint2 centerCoord)
{
    float4 centerSignal = LOAD_TEXTURE2D(CAELIX_ATROUS_SIGNAL_TEX, centerCoord);
    if (centerSignal.a <= 0.001f)
    {
        return centerSignal;
    }

    float4 centerNormalPacked = LOAD_TEXTURE2D(_NormalTex, centerCoord);
    float centerFaceHash = round(centerNormalPacked.b);
    float3 centerNormal = normalize(CaelixUnpackNormal(centerNormalPacked.rg));
    float centerDepth = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, centerCoord).r;
    float centerLuminance = CaelixLuminance(centerSignal.rgb);

    int stepWidth = max(_ATrousStepWidth, 1);

    // The centre surface as a plane, in view space. A view ray r hits the plane
    // through the centre point at the parameter that makes dot(n, t*r) constant,
    // so a tap's predicted depth is centerDepth * (n.r_center) / (n.r_tap) --
    // exact for any plane under any perspective, with no finite differences and
    // no dependence on the tap direction.
    float3 centerNormalView = mul((float3x3)_CaelixWorldToCamera, centerNormal);
    float centerNdotR = dot(centerNormalView, CaelixViewRay(float2(centerCoord) + 0.5f + _CaelixJitter.xy));
    float depthTolerance = max(_ATrousDepthTolerance, _ATrousRelativeDepthTolerance * abs(centerDepth));

    // The first iteration (step 1) skips the luminance weight entirely: at raw
    // 1spp the luminance channel IS the noise (no real edge information), and an
    // unweighted B3 kernel has an exact zero at the Nyquist frequency, which is
    // where STBN concentrates its energy (checkerboard pattern). Any luminance
    // weighting at step 1 breaks that zero and lets the checkerboard survive --
    // later iterations sample at even strides (same parity) and can never
    // remove it. Geometry (normal/depth) weights still protect real edges.
    bool useLuminanceWeight = stepWidth > 1;

    float radianceDenom = 1.0f;
    if (useLuminanceWeight)
    {
        // Local luminance std-dev (3x3 at the current step spacing) normalizes
        // the radiance edge-stopping weight. Without it, sun-disk HDR outliers
        // next to near-zero pixels reject their own smoothing.
        float momentSum = 0.0f;
        float momentSqSum = 0.0f;
        float momentCount = 0.0f;

        [unroll]
        for (int my = -1; my <= 1; my++)
        {
            [unroll]
            for (int mx = -1; mx <= 1; mx++)
            {
                uint2 momentCoord = CaelixClampCoord(int2(centerCoord) + int2(mx, my) * stepWidth);
                float4 momentSignal = LOAD_TEXTURE2D(CAELIX_ATROUS_SIGNAL_TEX, momentCoord);
                if (momentSignal.a <= 0.001f)
                {
                    continue;
                }

                float momentLuminance = CaelixLuminance(momentSignal.rgb);
                momentSum += momentLuminance;
                momentSqSum += momentLuminance * momentLuminance;
                momentCount += 1.0f;
            }
        }

        float luminanceStdDev = 0.0f;
        if (momentCount > 0.5f)
        {
            float luminanceMean = momentSum / momentCount;
            luminanceStdDev = sqrt(max(momentSqSum / momentCount - luminanceMean * luminanceMean, 0.0f));
        }

        radianceDenom = _ATrousRadianceSigma * luminanceStdDev + 0.01f;
    }

    // Sparse iterations (step > 1) jitter every non-center tap inside its own
    // step-sized cell (per pixel, per tap, per frame). This breaks the parity
    // lock of the a-trous hole pattern (structured checkerboard / grid-dot
    // artifacts around HDR outliers); the stochastic residue is unstructured
    // and averages out in temporal accumulation. stepWidth is a power of two.
    bool applyJitter = _ATrousJitterTaps != 0 && stepWidth > 1;
    uint jitterMask = (uint)(stepWidth - 1);
    int jitterHalf = stepWidth >> 1;
    uint pixelSeed = CaelixHashUint(
        centerCoord.x ^ (centerCoord.y << 16)
        ^ ((uint)_ATrousFrameIndex * 0x68bc21ebU)
        ^ ((uint)stepWidth * 0x02e5be93U));

    float3 signalSum = 0.0f;
    float weightSum = 0.0f;

    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            int2 sampleOffset = int2(x, y) * stepWidth;
            if (applyJitter && (x != 0 || y != 0))
            {
                uint tapHash = CaelixHashUint(pixelSeed + (uint)((y + 2) * 5 + (x + 2)));
                sampleOffset += int2((int)(tapHash & jitterMask), (int)((tapHash >> 16) & jitterMask)) - jitterHalf;
            }

            uint2 sampleCoord = CaelixClampCoord(int2(centerCoord) + sampleOffset);

            float4 sampleSignal = LOAD_TEXTURE2D(CAELIX_ATROUS_SIGNAL_TEX, sampleCoord);
            if (sampleSignal.a <= 0.001f)
            {
                continue;
            }

            float4 sampleNormalPacked = LOAD_TEXTURE2D(_NormalTex, sampleCoord);
            float sampleFaceHash = round(sampleNormalPacked.b);
            if (_ATrousUseFaceHash != 0 && sampleFaceHash != centerFaceHash)
            {
                continue;
            }

            float3 sampleNormal = normalize(CaelixUnpackNormal(sampleNormalPacked.rg));
            float normalWeight = pow(saturate(dot(centerNormal, sampleNormal)), _ATrousNormalPower);

            float sampleDepth = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, sampleCoord).r;
            float sampleNdotR = dot(centerNormalView, CaelixViewRay(float2(sampleCoord) + 0.5f + _CaelixJitter.xy));
            float depthWeight = 0.0f;
            if (sampleNdotR * centerNdotR > 1e-6f)              // tap ray must hit the centre's plane on the same side
            {
                float predictedDepth = centerDepth * centerNdotR / sampleNdotR;   // exact for a plane, any perspective
                depthWeight = exp(-abs(sampleDepth - predictedDepth) / depthTolerance);
            }

            float radianceWeight = 1.0f;
            if (useLuminanceWeight)
            {
                float sampleLuminance = CaelixLuminance(sampleSignal.rgb);
                radianceWeight = exp(-abs(sampleLuminance - centerLuminance) / radianceDenom);
            }

            float kernelWeight = CaelixATrousKernel(x) * CaelixATrousKernel(y);
            float weight = kernelWeight * normalWeight * depthWeight * radianceWeight;

            signalSum += sampleSignal.rgb * weight;
            weightSum += weight;
        }
    }

    if (weightSum <= 0.0001f)
    {
        return centerSignal;
    }

    return float4(signalSum / weightSum, centerSignal.a);
}

#endif
