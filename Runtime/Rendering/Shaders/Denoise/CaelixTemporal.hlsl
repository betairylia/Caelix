#ifndef CAELIX_TEMPORAL_INCLUDED
#define CAELIX_TEMPORAL_INCLUDED

// Temporal accumulation against last frame's reprojected history.
//
// The current-frame signal is named by CAELIX_TEMPORAL_SIGNAL_TEX, which the including shader
// must declare as a TEXTURE2D before including this file. The history half is always the
// double-buffered CaelixCameraHistory target, so its uniform names are fixed here.
//
// Signal convention on the way in:  .rgb = quantity,   .a = validity (> 0.001 means "has data").
// On the way out and in history:    .rgb = quantity,   .a = accumulated frame count (>= 1).
//
// Depth rejection is an exact PLANE PREDICTION in the PREVIOUS frame's view, not a scalar depth
// comparison. The reprojected sample point and the history tap sit at different places on the
// screen, so on any surface not perpendicular to the view their depths legitimately differ -- a
// scalar tolerance then either rejects a whole band of a receding floor or accepts a genuinely
// different surface. Predicting the tap's depth from the current surface's plane removes both:
// the residual is zero on the surface the sample belongs to, whatever its slope.

#include "CaelixDenoiseCommon.hlsl"

#ifndef CAELIX_TEMPORAL_SIGNAL_TEX
#define CAELIX_TEMPORAL_SIGNAL_TEX _IndirectRadianceTex
#endif

TEXTURE2D(_PreviousIndirectRadianceHistoryTex);
TEXTURE2D(_PreviousDepthHistoryTex);
TEXTURE2D(_PreviousNormalHistoryTex);

int _IndirectRadianceHistoryValid;
int _TemporalRadianceEnabled;
int _TemporalRadianceBilinearHistory;
int _TemporalRadianceDepthRejectionEnabled;
int _TemporalRadianceNormalRejectionEnabled;
float _TemporalRadianceCurrentFrameMinWeight;
float _TemporalRadianceDepthTolerance;
float _TemporalRadianceRelativeDepthTolerance;
float _TemporalRadianceNormalThreshold;
float _TemporalRadianceMaxFrames;

// History alpha stores the per-pixel accumulated frame count (>= 1 when valid).
// A tap is only blended in when its stored previous-frame depth matches the depth
// the current surface's plane predicts AT THAT TAP, in the previous frame's view.
// The plane is anchored by expectedPreviousDepth -- the depth this surface is
// *known* to have had last frame, computed by the raygen alongside the motion
// vectors -- so the test stays exact under camera motion.
bool CaelixValidateHistoryTap(
    uint2 tapCoord,
    float expectedPreviousDepth,
    float3 currentNormal,
    float3 normalPrevView,
    float prevNdotR,
    out float4 history)
{
    history = LOAD_TEXTURE2D(_PreviousIndirectRadianceHistoryTex, tapCoord);
    if (history.a <= 0.5f)
    {
        return false;
    }

    if (_TemporalRadianceDepthRejectionEnabled != 0)
    {
        // The tap was traced through the PREVIOUS frame's jitter, so that is the ray to intersect.
        float tapNdotR = dot(normalPrevView, CaelixViewRay(float2(tapCoord) + 0.5f + _CaelixJitter.zw));
        if (tapNdotR * prevNdotR <= 1e-6f)          // tap ray misses the plane, or hits it behind the camera
        {
            return false;
        }

        float previousDepth = LOAD_TEXTURE2D(_PreviousDepthHistoryTex, tapCoord).r;
        float predictedDepth = expectedPreviousDepth * prevNdotR / tapNdotR;
        float tolerance = max(_TemporalRadianceDepthTolerance, abs(expectedPreviousDepth) * _TemporalRadianceRelativeDepthTolerance);
        if (abs(previousDepth - predictedDepth) > tolerance)
        {
            return false;
        }
    }

    if (_TemporalRadianceNormalRejectionEnabled != 0)
    {
        float4 previousNormalPacked = LOAD_TEXTURE2D(_PreviousNormalHistoryTex, tapCoord);
        if (previousNormalPacked.a <= 0.001f)
        {
            return false;
        }

        float3 previousNormal = normalize(CaelixUnpackNormal(previousNormalPacked.rg));
        if (dot(previousNormal, currentNormal) < _TemporalRadianceNormalThreshold)
        {
            return false;
        }
    }

    return true;
}

float4 CaelixTemporalAccumulate(uint2 coord)
{
    float4 currentSignal = LOAD_TEXTURE2D(CAELIX_TEMPORAL_SIGNAL_TEX, coord);
    float currentValid = currentSignal.a > 0.001f ? 1.0f : 0.0f;
    if (_TemporalRadianceEnabled == 0 || _IndirectRadianceHistoryValid == 0 || currentValid == 0.0f)
    {
        return float4(currentSignal.rgb, currentValid);
    }

    float2 currentUV = (float2(coord) + 0.5f) / CaelixHistoryScale();
    // Motion is stored previous-minus-current (NRD convention), so reprojection adds it.
    float2 motionVector = LOAD_TEXTURE2D(_MotionVectorTex, coord).rg;
    float2 previousUV = currentUV + motionVector;
    bool canReproject =
        all(previousUV >= float2(0.0f, 0.0f)) &&
        all(previousUV <= float2(1.0f, 1.0f));

    if (!canReproject)
    {
        return float4(currentSignal.rgb, 1.0f);
    }

    float4 currentNormalHistory = LOAD_TEXTURE2D(_CurrentNormalHistoryTex, coord);
    float3 currentNormal = normalize(CaelixUnpackNormal(currentNormalHistory.rg));
    float expectedPreviousDepth = currentNormalHistory.b;

    // The surface's plane as the previous frame saw it, anchored at this sample point's own
    // previous position. The stored motion vector was un-jittered by THIS frame's jitter (the
    // raygen subtracted a jittered launch position), so adding it back recovers the exact
    // launch-space position the sample point projected to last frame.
    float3 normalPrevView = mul((float3x3)_CaelixPrevWorldToCamera, currentNormal);
    float2 prevLaunchPos = previousUV * CaelixHistoryScale() + _CaelixJitter.xy;
    float prevNdotR = dot(normalPrevView, CaelixViewRay(prevLaunchPos));

    float2 historyCoord = previousUV * CaelixHistoryScale() - 0.5f;

    float3 historySignal = 0.0f;
    float historyFrames = 0.0f;
    float weightSum = 0.0f;

    if (_TemporalRadianceBilinearHistory != 0)
    {
        // Manual bilinear with per-tap validation: taps that fail the
        // depth/normal tests drop out and the remaining weights renormalize,
        // so history never blends across silhouettes.
        int2 baseCoord = (int2)floor(historyCoord);
        float2 blend = frac(historyCoord);

        const int2 tapOffsets[4] = { int2(0, 0), int2(1, 0), int2(0, 1), int2(1, 1) };
        float tapWeights[4] = {
            (1.0f - blend.x) * (1.0f - blend.y),
            blend.x * (1.0f - blend.y),
            (1.0f - blend.x) * blend.y,
            blend.x * blend.y
        };

        [unroll]
        for (int tap = 0; tap < 4; tap++)
        {
            if (tapWeights[tap] <= 0.0001f)
            {
                continue;
            }

            uint2 tapCoord = CaelixClampCoord(baseCoord + tapOffsets[tap]);
            float4 history;
            if (!CaelixValidateHistoryTap(
                    tapCoord, expectedPreviousDepth, currentNormal, normalPrevView, prevNdotR, history))
            {
                continue;
            }

            historySignal += history.rgb * tapWeights[tap];
            historyFrames += history.a * tapWeights[tap];
            weightSum += tapWeights[tap];
        }
    }
    else
    {
        uint2 tapCoord = CaelixClampCoord((int2)round(historyCoord));
        float4 history;
        if (CaelixValidateHistoryTap(
                tapCoord, expectedPreviousDepth, currentNormal, normalPrevView, prevNdotR, history))
        {
            historySignal = history.rgb;
            historyFrames = history.a;
            weightSum = 1.0f;
        }
    }

    if (weightSum <= 0.0001f)
    {
        return float4(currentSignal.rgb, 1.0f);
    }

    historySignal /= weightSum;
    historyFrames = min(historyFrames / weightSum, max(_TemporalRadianceMaxFrames - 1.0f, 0.0f));

    float currentFrameWeight = max(_TemporalRadianceCurrentFrameMinWeight, 1.0f / (historyFrames + 1.0f));
    float3 blended = lerp(historySignal, currentSignal.rgb, currentFrameWeight);
    return float4(blended, historyFrames + 1.0f);
}

#endif
