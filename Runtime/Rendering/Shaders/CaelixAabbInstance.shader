// Placeholder material for the voxel AABB instances.
//
// RayTracingAABBsInstanceConfig requires a Material, so every render group's instance is added with
// one. The inline ray query path never runs a hit group: the brick DDA runs inside the trace kernel
// (CaelixRayQueryTrace.hlsl) on each procedural candidate, and the kernel commits the hit itself.
// So this shader exists only to satisfy that requirement, and both of its entry points are
// deliberately empty: the intersection shader reports nothing and the closest-hit shader leaves the
// payload untouched.
Shader "Caelix/AabbInstance"
{
    SubShader
    {
        Pass
        {
            Name "Caelix"
            Tags{
                "LightMode" = "RayTracing"
            }

            HLSLPROGRAM
            #pragma target 6.6
            #pragma use_dxc
            #pragma raytracing test
            #pragma multi_compile_local RAY_TRACING_PROCEDURAL_GEOMETRY

            #include "RayPayload.hlsl"

            struct CaelixAabbAttributes
            {
                uint unused;
            };

            #if RAY_TRACING_PROCEDURAL_GEOMETRY
            [shader("intersection")]
            void IntersectionMain()
            {
                // No ReportHit: nothing ever hits through this material.
                return;
            }
            #endif

            [shader("closesthit")]
            void ClosestHitMain(
                inout RayPayload payload : SV_RayPayload,
                CaelixAabbAttributes attribs : SV_IntersectionAttributes)
            {
                // Unreachable, because the intersection shader never reports a hit. The payload is
                // left exactly as the caller cleared it.
            }

            ENDHLSL
        }
    }
}
