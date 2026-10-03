Shader "Caelix/GI Prototype Present"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            TEXTURE2D(_GiDepth);
            float4 Frag(Varyings input, out float depth : SV_Depth) : SV_Target
            {
                float eyeDepth = SAMPLE_TEXTURE2D(_GiDepth, sampler_PointClamp, input.texcoord).r;
                depth = eyeDepth <= 0.0 ? UNITY_RAW_FAR_CLIP_VALUE
                    : saturate((1.0 / eyeDepth - _ZBufferParams.w) / _ZBufferParams.z);
                return float4(SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
