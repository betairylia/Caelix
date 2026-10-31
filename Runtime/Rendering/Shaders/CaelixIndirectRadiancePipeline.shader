Shader "Hidden/Caelix/IndirectRadiancePipeline"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // The working signal of this chain: the combined diffuse+specular stochastic radiance.
        // Both CAELIX_ATROUS_SIGNAL_TEX and CAELIX_TEMPORAL_SIGNAL_TEX default to it.
        TEXTURE2D(_IndirectRadianceTex);

        #include "Denoise/CaelixTemporal.hlsl"
        #include "Denoise/CaelixCrossResolve.hlsl"
        #include "Denoise/CaelixColorResolve.hlsl"

        TEXTURE2D(_DeterministicRadianceTex);
        TEXTURE2D(_DiffuseRadianceTex);
        TEXTURE2D(_SpecularRadianceTex);
        TEXTURE2D(_AlbedoTex);
        TEXTURE2D(_AccumulatedIndirectRadianceTex);

        int _SpatialFilterEnabled;
        int _SeparableFilterRadius;
        float _SeparableFilterDistanceSigma;

        float4 SpatialFilter(float2 uv, int2 filterDirection)
        {
            uint2 centerCoord = CaelixPixelCoord(uv);
            float4 centerIndirect = LOAD_TEXTURE2D(_IndirectRadianceTex, centerCoord);
            if (_SpatialFilterEnabled == 0 || centerIndirect.a <= 0.001f)
            {
                return centerIndirect;
            }

            float4 centerNormalPacked = LOAD_TEXTURE2D(_NormalTex, centerCoord);
            float centerFaceHash = round(centerNormalPacked.b);
            if (centerFaceHash <= 0.0f)
            {
                return centerIndirect;
            }

            float3 radianceSum = centerIndirect.rgb;
            float weightSum = 1.0f;

            [unroll]
            for (int offset = -7; offset <= 7; offset++)
            {
                if (offset == 0 || abs(offset) > _SeparableFilterRadius)
                {
                    continue;
                }

                uint2 sampleCoord = CaelixClampCoord(int2(centerCoord) + filterDirection * offset);
                float4 sampleIndirect = LOAD_TEXTURE2D(_IndirectRadianceTex, sampleCoord);
                float4 sampleNormalPacked = LOAD_TEXTURE2D(_NormalTex, sampleCoord);
                float sampleFaceHash = round(sampleNormalPacked.b);

                bool accept =
                    sampleIndirect.a > 0.001f &&
                    sampleFaceHash == centerFaceHash;

                if (accept)
                {
                    float distance = abs(float(offset));
                    float weight = exp(-(distance * distance) / max(_SeparableFilterDistanceSigma, 0.0001f));
                    radianceSum += sampleIndirect.rgb * weight;
                    weightSum += weight;
                }
            }

            return float4(radianceSum / max(weightSum, 0.0001f), centerIndirect.a);
        }

        float4 SpatialFilterX(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return SpatialFilter(input.texcoord, int2(1, 0));
        }

        float4 SpatialFilterY(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return SpatialFilter(input.texcoord, int2(0, 1));
        }

        float4 TemporalAccumulation(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return CaelixTemporalAccumulate(CaelixPixelCoord(input.texcoord));
        }

        float4 Composite(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            uint2 coord = CaelixPixelCoord(input.texcoord);
            float4 deterministicRadiance = LOAD_TEXTURE2D(_DeterministicRadianceTex, coord);
            float4 albedo = LOAD_TEXTURE2D(_AlbedoTex, coord);
            float4 indirectRadiance = LOAD_TEXTURE2D(_AccumulatedIndirectRadianceTex, coord);

            float3 result = deterministicRadiance.rgb + albedo.rgb * indirectRadiance.rgb;
            // float3 result = indirectRadiance.rgb;
            return float4(result, deterministicRadiance.a);
        }

        float4 CrossResolve(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return CaelixCrossResolve(CaelixPixelCoord(input.texcoord));
        }

        float4 ColorResolve(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return CaelixColorResolve(CaelixPixelCoord(input.texcoord));
        }

        // Sums the split stochastic targets back into the single combined signal the legacy
        // spatial/temporal chain consumes. The split targets carry hit distance in .a, so the
        // chain's validity flag (alpha) is re-derived from the deterministic target's hit/miss
        // alpha instead.
        float4 CombineStochastic(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            uint2 coord = CaelixPixelCoord(input.texcoord);
            float3 diffuse = LOAD_TEXTURE2D(_DiffuseRadianceTex, coord).rgb;
            float3 specular = LOAD_TEXTURE2D(_SpecularRadianceTex, coord).rgb;
            float validity = LOAD_TEXTURE2D(_DeterministicRadianceTex, coord).a > 0.001f ? 1.0f : 0.0f;
            return float4(diffuse + specular, validity);
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "SpatialFilterX"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment SpatialFilterX
            ENDHLSL
        }

        Pass
        {
            Name "SpatialFilterY"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment SpatialFilterY
            ENDHLSL
        }

        Pass
        {
            Name "TemporalAccumulation"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment TemporalAccumulation
            ENDHLSL
        }

        Pass
        {
            Name "Composite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Composite
            ENDHLSL
        }

        Pass
        {
            Name "CombineStochastic"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CombineStochastic
            ENDHLSL
        }

        Pass
        {
            Name "CrossResolve"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CrossResolve
            ENDHLSL
        }

        Pass
        {
            Name "ColorResolve"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment ColorResolve
            ENDHLSL
        }
    }
}
