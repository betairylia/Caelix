// Present and motion-vector passes of the native Vulkan renderer (CaelixNativeRendererFeature).
//
// Inputs (globals bound by the feature): the plugin targets, written by the library with
// CAELIX_RENDER_FLAG_FLIP_Y so their rows are in Unity's order (no flip here):
//   _CaelixNativeColor   RGBA16F, camera size: lighting modes = premultiplied linear colour, at the
//                        misses the background input (the downsampled camera colour after Unity's
//                        skybox); with an upscaler the upscaled image; primary mode = a debug view
//                        written as it is (display values)
//   _CaelixNativeDepth   R32F, camera size in every case (with an upscaler the library writes it from
//                        unjittered centre rays, aligned with the voxel id): device depth of Unity's
//                        GPU projection (reversed Z on Vulkan); a miss is the far plane
//   _CaelixNativeMotion  RGBA16F, geometry size: xy = previous - current pixel, y UP (the flip
//                        negates the library's y-down motion), unjittered surface motion
//   _CaelixNativeAlbedo  RGBA8, geometry size: a = coverage (1 hit, 0 miss) in every mode
//   _CaelixNativeVoxelId R32_UINT, camera size: the voxel hash of the camera ray's first hit,
//                        0xFFFFFFFF on a miss (read only in mask mode)
//   _CaelixPrimaryView   1 = the colour holds display values: decode sRGB, so that Unity's own
//                        linear -> sRGB output encode shows the stored value once
//   _CaelixUseVoxelIdMask 1 = mask mode: the voxel id decides which pixels are voxels
//   _CaelixMaskDilate    1 = the view upscales this frame: the mask is dilated by one pixel
//
// Pass order: after Unity's opaques and its full-resolution skybox (the render event runs after the
// skybox too). Mask rule (VoxelHit): in mask mode a pixel is a voxel when its voxel id is a hit; with
// _CaelixMaskDilate also when one of its 8 neighbours is. The ring pixels of the dilation carry the
// upscaler's blend of scene and background and the miss depth (far), so writing them is correct;
// the pixels beyond the ring keep Unity's own skybox. Without mask mode: coverage >= 0.99 (pass 0),
// >= 0.5 (pass 2).
// Pass 0 (stage 0, after the skybox + 1): VoxelHit, colour + SV_Depth, depth-tested.
// Pass 1 (stage 1, after the skybox + 2, only without mask mode): 0.01 <= coverage < 0.99,
//        premultiplied blend, no depth write.
// Pass 2 (motion, after URP's motion vector pass): VoxelHit and the voxel depth passes the depth
//        test against the camera depth (a raster object in front keeps its own motion); writes URP's
//        motion vector convention:
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
        Texture2D<uint> _CaelixNativeVoxelId;
        float _CaelixPrimaryView;
        float _CaelixUseVoxelIdMask;
        float _CaelixMaskDilate;

        // CaelixRenderTargets::voxel_id of a miss.
        static const uint kVoxelIdMiss = 0xFFFFFFFFu;

        // Point-sampled by UV: the colour and the depth have the camera's size, motion and albedo the
        // camera's size or the upscaler's render size (UV sampling scales them), and depth or motion
        // must never be interpolated across a silhouette.
        float4 SampleTarget(Texture2D tex, float2 uv)
        {
            return SAMPLE_TEXTURE2D_LOD(tex, sampler_PointClamp, uv, 0);
        }

        float Coverage(float2 uv) { return SampleTarget(_CaelixNativeAlbedo, uv).a; }

        // The voxel id at pixel p (clamped to the image) is a hit.
        bool VoxelIdHitAt(int2 p, int2 size)
        {
            p = clamp(p, int2(0, 0), size - 1);
            return _CaelixNativeVoxelId.Load(int3(p, 0)) != kVoxelIdMiss;
        }

        // The pixel at uv shows a voxel (see the mask rule at the top). Without mask mode: coverage
        // >= minCoverage.
        bool VoxelHit(float2 uv, float minCoverage)
        {
            // One return: FXC warns about an early return here (X4000).
            bool hit;
            if (_CaelixUseVoxelIdMask > 0.5)
            {
                uint2 size;
                _CaelixNativeVoxelId.GetDimensions(size.x, size.y);
                int2 isize = int2(size);
                int2 p = int2(uv * float2(size));
                hit = VoxelIdHitAt(p, isize);
                if (_CaelixMaskDilate > 0.5)
                {
                    [unroll]
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        [unroll]
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            hit = hit || VoxelIdHitAt(p + int2(dx, dy), isize);
                        }
                    }
                }
            }
            else
            {
                hit = Coverage(uv) >= minCoverage;
            }
            return hit;
        }

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
            // Without mask mode, partial coverage is stage 1's (a premultiplied blend over the skybox).
            if (!VoxelHit(uv, 0.99))
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
            if (!VoxelHit(uv, 0.5))
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
