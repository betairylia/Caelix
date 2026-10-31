Shader "Caelix/PostFlip"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        // The Blit.hlsl file provides the vertex shader (Vert),
        // the input structure (Attributes), and the output structure (Varyings)
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // Camera depth always comes from the G-buffer depth target, whatever is being displayed,
        // so scene geometry keeps depth-testing correctly against a debug view.
        TEXTURE2D(_DepthTex);
        SAMPLER(sampler_DepthTex);

        // Must match the CaelixDebugView enum (CaelixRenderSettings.cs). The present stage binds
        // the matching buffer as _BlitTexture, so this only selects how to decode it.
        #define CAELIX_DEBUG_REGULAR                       0
        #define CAELIX_DEBUG_MOTION_VECTOR                 1
        #define CAELIX_DEBUG_ALBEDO                        2
        #define CAELIX_DEBUG_NORMAL                        3
        #define CAELIX_DEBUG_DEPTH                         4
        #define CAELIX_DEBUG_DETERMINISTIC_RADIANCE        5
        #define CAELIX_DEBUG_INDIRECT_RADIANCE_RAW         6
        #define CAELIX_DEBUG_INDIRECT_RADIANCE_FILTERED    7
        #define CAELIX_DEBUG_INDIRECT_RADIANCE_ACCUMULATED 8
        #define CAELIX_DEBUG_STOCHASTIC_DIFFUSE            9
        #define CAELIX_DEBUG_STOCHASTIC_SPECULAR           10

        int _DebugView;

        // 0 = the opaque body of the image, drawn before the skybox so it writes camera depth for
        // everything that reads _CameraDepthTexture. 1 = the partially covered silhouette pixels the
        // colour resolve produced, drawn AFTER the skybox because they have to blend against it.
        // The two stages partition the image by alpha, so no pixel is drawn twice.
        int _CaelixPresentStage;

        // The Caelix G-buffer carries linear view depth, but SV_Depth wants a raw
        // (post-projection, platform-convention) depth. This is the inverse of LinearEyeDepth,
        // which is 1/(z*raw + w) -- this conversion belongs here, at the only point the depth is
        // consumed as a depth-buffer value.
        float CaelixEyeDepthToRawDepth(float eyeZ)
        {
            // saturate() pins beyond-far hits onto the far plane under either depth convention:
            // reversed-Z produces a small negative value there, non-reversed slightly over 1.
            return saturate((1.0f / max(eyeZ, 1e-6f) - _ZBufferParams.w) / _ZBufferParams.z);
        }

        float4 Flip (Varyings input, out float outDepth : SV_Depth) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = input.texcoord;

            float4 source = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);
            // Point-sampled: depth is discontinuous at silhouettes, so interpolating it would
            // produce in-between values that exist on no surface.
            float linearViewDepth = SAMPLE_TEXTURE2D(_DepthTex, sampler_PointClamp, uv).r;
            float rawDepth = CaelixEyeDepthToRawDepth(linearViewDepth);
            outDepth = rawDepth;

            if (_CaelixPresentStage == 1)
            {
                // Only the silhouette band, and only for the regular view: every debug view is
                // shown opaque and has already been drawn in full by stage 0.
                if (_DebugView != CAELIX_DEBUG_REGULAR || source.a < 0.01f || source.a >= 0.99f)
                {
                    clip(-1);
                }

                // Premultiplied by coverage already, so the blend state does the rest.
                return source;
            }

            if (_DebugView == CAELIX_DEBUG_MOTION_VECTOR)
            {
                // Raw buffer contents, so this reads in the stored NRD convention
                // (previousUV - currentUV): the colour points back to where the surface came from.
                float2 encodedMotion = source.rg * 64.0f;
                return float4(
                    saturate(0.5f + encodedMotion.x),
                    saturate(0.5f + encodedMotion.y),
                    saturate(0.5f - max(abs(encodedMotion.x), abs(encodedMotion.y))),
                    1.0f);
            }

            if (_DebugView == CAELIX_DEBUG_NORMAL)
            {
                // Normals are stored octahedral-packed in .xy over [0,1].
                float3 normal = UnpackNormalOctQuadEncode(source.xy * 2.0f - 1.0f);
                return float4(normal * 0.5f + 0.5f, 1.0f);
            }

            if (_DebugView == CAELIX_DEBUG_DEPTH)
            {
                // The buffer holds linear view depth in world units, which would blow out to white,
                // so show the converted raw depth instead -- it spans [0,1] and is what actually
                // reaches the depth buffer. On reversed-Z, near surfaces read bright.
                return float4(rawDepth.rrr, 1.0f);
            }

            if (_DebugView != CAELIX_DEBUG_REGULAR)
            {
                // Albedo and the radiance buffers are all plain colour; show them opaque so empty
                // regions read as black instead of letting the scene behind show through.
                return float4(source.rgb, 1.0f);
            }

            // Anything short of full coverage is left to stage 1: blending it here would blend it
            // against whatever the camera target held before the skybox was drawn.
            if (source.a < 0.99f)
            {
                clip(-1);
            }

            return source;
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100
        ZWrite On Cull Off

        Pass
        {
            Name "CaelixPresent"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Flip

            ENDHLSL
        }

        Pass
        {
            Name "CaelixPresentEdges"

            // Premultiplied-alpha blend, because the colour resolve's partial-coverage pixels carry
            // colour already scaled by their coverage. ZTest LEqual keeps them behind any opaque
            // geometry drawn in between; ZWrite still fills in the depth stage 0 clipped away.
            Blend One OneMinusSrcAlpha
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Flip

            ENDHLSL
        }
    }
}
