#ifndef CAELIX_COLOR_RESOLVE_INCLUDED
#define CAELIX_COLOR_RESOLVE_INCLUDED

// TAA-style resolve of the final composited colour against its own reprojected history.
//
// The primary rays carry one global sub-pixel offset per frame (CaelixCameraHistory's Halton
// cycle). On its own that only moves the aliasing around; this pass is what turns it into
// anti-aliasing, by accumulating the offset samples over the cycle.
//
// It runs on colour rather than on any G-buffer for the same reason the cross resolve does: at a
// silhouette the guides describe different surfaces on either side, and only the composited colour
// has collapsed them into one value. The colour is PREMULTIPLIED by coverage -- sky pixels are
// (0,0,0,0), hits are alpha 1 -- so alpha accumulates exactly like the colour does and comes out as
// fractional coverage on the silhouette pixels. That partial alpha is what the second present stage
// blends against the URP skybox.
//
// History is fetched with a 5-tap Catmull-Rom filter (bilinear taps) rather than plain bilinear:
// bilinear resampling every frame is a low-pass filter applied over and over, and it visibly melts
// the image within the length of the accumulation window.
//
// The neighbourhood clip is the standard variance box in YCoCg: clipping (moving the history
// towards the mean until it enters the box) rather than clamping (snapping each channel
// independently) keeps the hue of a rejected sample instead of turning it into a colour that is in
// no way present in the neighbourhood.

#include "CaelixDenoiseCommon.hlsl"

TEXTURE2D(_PreviousColorHistoryTex);

int _ColorHistoryValid;
float _ColorResolveBlend;
float _ColorResolveClipScale;

float3 CaelixRGBToYCoCg(float3 rgb)
{
    return float3(
         0.25f * rgb.r + 0.5f * rgb.g + 0.25f * rgb.b,
         0.5f  * rgb.r                - 0.5f  * rgb.b,
        -0.25f * rgb.r + 0.5f * rgb.g - 0.25f * rgb.b);
}

float3 CaelixYCoCgToRGB(float3 ycocg)
{
    return float3(
        ycocg.x + ycocg.y - ycocg.z,
        ycocg.x           + ycocg.z,
        ycocg.x - ycocg.y - ycocg.z);
}

// 5-tap Catmull-Rom: the 4x4 kernel collapses onto 5 bilinear fetches by exploiting that the two
// inner weights of each axis can be taken as one offset tap. The corner taps are dropped, which is
// why the weights are renormalized.
float4 CaelixSampleCatmullRom(TEXTURE2D_PARAM(tex, smp), float2 uv, float2 size)
{
    float2 samplePos = uv * size;
    float2 texPos1 = floor(samplePos - 0.5f) + 0.5f;
    float2 f = samplePos - texPos1;

    float2 w0 = f * (-0.5f + f * (1.0f - 0.5f * f));
    float2 w1 = 1.0f + f * f * (-2.5f + 1.5f * f);
    float2 w2 = f * (0.5f + f * (2.0f - 1.5f * f));
    float2 w3 = f * f * (-0.5f + 0.5f * f);

    float2 w12 = w1 + w2;
    float2 offset12 = w2 / w12;

    float2 texelSize = 1.0f / size;
    float2 texPos0 = (texPos1 - 1.0f) * texelSize;
    float2 texPos3 = (texPos1 + 2.0f) * texelSize;
    float2 texPos12 = (texPos1 + offset12) * texelSize;

    float4 result = 0.0f;
    float weight = 0.0f;

    #define CR_TAP(p, w) result += SAMPLE_TEXTURE2D_LOD(tex, smp, p, 0) * (w); weight += (w);
    CR_TAP(float2(texPos12.x, texPos0.y),  w12.x * w0.y)
    CR_TAP(float2(texPos0.x,  texPos12.y), w0.x  * w12.y)
    CR_TAP(float2(texPos12.x, texPos12.y), w12.x * w12.y)
    CR_TAP(float2(texPos3.x,  texPos12.y), w3.x  * w12.y)
    CR_TAP(float2(texPos12.x, texPos3.y),  w12.x * w3.y)
    #undef CR_TAP

    return result / weight;
}

float4 CaelixColorResolve(uint2 centerCoord)
{
    float4 current = LOAD_TEXTURE2D_X(_BlitTexture, centerCoord);

    // Neighbourhood statistics of the current frame, in YCoCg for the RGB half and as a plain
    // min/max for coverage. Coverage is not a colour and has no variance to speak of; a hard
    // interval is both cheaper and tighter.
    float3 momentSum = 0.0f;
    float3 momentSqSum = 0.0f;
    float alphaMin = current.a;
    float alphaMax = current.a;

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float4 tap = LOAD_TEXTURE2D_X(_BlitTexture, CaelixClampCoord(int2(centerCoord) + int2(x, y)));
            float3 tapYCoCg = CaelixRGBToYCoCg(tap.rgb);
            momentSum += tapYCoCg;
            momentSqSum += tapYCoCg * tapYCoCg;
            alphaMin = min(alphaMin, tap.a);
            alphaMax = max(alphaMax, tap.a);
        }
    }

    float3 mean = momentSum / 9.0f;
    float3 stdDev = sqrt(max(momentSqSum / 9.0f - mean * mean, 0.0f));
    float3 boxExtent = _ColorResolveClipScale * stdDev;

    if (_ColorHistoryValid == 0)
    {
        return current;
    }

    float2 size = CaelixHistoryScale();
    float2 previousUV = (float2(centerCoord) + 0.5f) / size + LOAD_TEXTURE2D(_MotionVectorTex, centerCoord).xy;
    if (any(previousUV < 0.0f) || any(previousUV > 1.0f))
    {
        // Off screen last frame: there is nothing to accumulate against, so this pixel restarts.
        return current;
    }

    float4 history = CaelixSampleCatmullRom(
        TEXTURE2D_ARGS(_PreviousColorHistoryTex, sampler_LinearClamp), previousUV, size);

    // Clip towards the centre of the box rather than clamping per channel: the direction of the
    // history's offset from the neighbourhood mean is preserved, only its length is cut.
    float3 historyYCoCg = CaelixRGBToYCoCg(history.rgb);
    float3 offset = historyYCoCg - mean;
    float3 unitOffset = abs(offset) / max(boxExtent, 1e-6f);
    float maxUnitOffset = max(unitOffset.x, max(unitOffset.y, unitOffset.z));
    if (maxUnitOffset > 1.0f)
    {
        historyYCoCg = mean + offset / maxUnitOffset;
    }

    history.rgb = CaelixYCoCgToRGB(historyYCoCg);
    history.a = clamp(history.a, alphaMin, alphaMax);

    // Inverse-luminance weighting (Karis): without it a single very bright sample entering the
    // history dominates the average for the whole window and reads as a flicker that decays.
    // Coverage rides on the same weights so it stays consistent with the colour it belongs to.
    float currentWeight = _ColorResolveBlend / (1.0f + CaelixLuminance(current.rgb));
    float historyWeight = (1.0f - _ColorResolveBlend) / (1.0f + CaelixLuminance(history.rgb));

    return (current * currentWeight + history * historyWeight) / max(currentWeight + historyWeight, 1e-6f);
}

#endif
