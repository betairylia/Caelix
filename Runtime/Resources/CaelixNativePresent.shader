// Present and motion-vector passes of the native Vulkan renderer (CaelixNativeRendererFeature).
//
// Inputs (globals bound by the feature): the four plugin targets, written by the library with
// CAELIX_RENDER_FLAG_FLIP_Y so their rows are in Unity's order (no flip here):
//   _CaelixNativeColor   RGBA16F: lighting modes = premultiplied linear colour; primary mode = a debug
//                        view written as it is (display values)
//   _CaelixNativeDepth   R32F: device depth of Unity's GPU projection (reversed Z on Vulkan); a miss
//                        is the far plane
//   _CaelixNativeMotion  RGBA16F: xy = previous - current pixel, y UP (the flip negates the library's
//                        y-down motion), unjittered surface motion
//   _CaelixNativeAlbedo  RGBA8: a = coverage (1 hit, 0 miss) in every mode
//   _CaelixPrimaryView   1 = the colour holds display values: decode sRGB, so that Unity's own
//                        linear -> sRGB output encode shows the stored value once
//
// Pass 0 (stage 0, after the opaques): coverage >= 0.99, colour + SV_Depth, depth-tested.
// Pass 1 (stage 1, after the skybox): 0.01 <= coverage < 0.99, premultiplied blend, no depth write.
// Pass 2 (motion, after URP's motion vector pass): coverage >= 0.5 and the voxel depth passes the
//        depth test against the camera depth (a raster object in front keeps its own motion);
//        writes URP's motion vector convention:
//          URP (MotionVectorsCommon.hlsl, CalcNdcMotionVectorFromCsPositions):
//            mv = (posNDC - prevPosNDC) * 0.5, y negated when UNITY_UV_STARTS_AT_TOP
//          i.e. current UV - previous UV with v up, unjittered. Ours: m = previous - current in
//          pixels with y up, so mv = -m / size.
Shader "Hidden/Caelix/NativePresent"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        // Vert, Attributes, Varyings, _BlitTexture.
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D(_CaelixNativeColor);
        TEXTURE2D(_CaelixNativeDepth);
        TEXTURE2D(_CaelixNativeMotion);
        TEXTURE2D(_CaelixNativeAlbedo);
        float _CaelixPrimaryView;

        // Point-sampled by UV: the colour has the camera's size, depth, motion and albedo the camera's
        // size or the upscaler's render size (UV sampling scales them), and depth or motion must
        // never be interpolated across a silhouette.
        float4 SampleTarget(Texture2D tex, float2 uv)
        {
            return SAMPLE_TEXTURE2D_LOD(tex, sampler_PointClamp, uv, 0);
        }

        float Coverage(float2 uv) { return SampleTarget(_CaelixNativeAlbedo, uv).a; }

        float4 DisplayColor(float2 uv)
        {
            float4 color = SampleTarget(_CaelixNativeColor, uv);
            if (_CaelixPrimaryView > 0.5)
            {
                color.rgb = SRGBToLinear(saturate(color.rgb));
            }
            return color;
        }

        float4 FragPresent(Varyings input, out float outDepth : SV_Depth) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            outDepth = SampleTarget(_CaelixNativeDepth, uv).r;
            // Partial coverage is stage 1's: blending here would blend against what the camera
            // target held before the skybox.
            if (Coverage(uv) < 0.99)
            {
                clip(-1);
            }
            return float4(DisplayColor(uv).rgb, 1.0);
        }

        float4 FragPresentEdges(Varyings input, out float outDepth : SV_Depth) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            outDepth = SampleTarget(_CaelixNativeDepth, uv).r;
            float coverage = Coverage(uv);
            if (coverage < 0.01 || coverage >= 0.99)
            {
                clip(-1);
            }
            // The composite's colour is premultiplied by coverage already.
            return float4(DisplayColor(uv).rgb, coverage);
        }

        float4 FragMotion(Varyings input, out float outDepth : SV_Depth) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            outDepth = SampleTarget(_CaelixNativeDepth, uv).r;
            if (Coverage(uv) < 0.5)
            {
                clip(-1);
            }
            uint width, height;
            _CaelixNativeMotion.GetDimensions(width, height);
            float2 motion = SampleTarget(_CaelixNativeMotion, uv).xy;
            return float4(-motion / float2(width, height), 0.0, 0.0);
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off

        Pass
        {
            Name "CaelixNativePresent"
            // LEqual is turned into GEqual by Unity on reversed-Z platforms.
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPresent
            ENDHLSL
        }

        Pass
        {
            Name "CaelixNativePresentEdges"
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPresentEdges
            ENDHLSL
        }

        Pass
        {
            Name "CaelixNativeMotion"
            ZWrite Off
            ZTest LEqual
            ColorMask RG

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMotion
            ENDHLSL
        }
    }
}
