#ifndef CAELIX_GI_BSDF_INCLUDED
#define CAELIX_GI_BSDF_INCLUDED

// Directions point away from the surface. PDFs use solid angle, except samples
// marked delta, whose PDF is the discrete probability of their selected event.
#define GI_PI 3.14159265358979323846

struct GiBsdf
{
    float3 baseColor;
    float3 emission;
    float roughness;
    float metallic;
    float etaI;
    float etaT;
    uint transparent;
    uint destinationMedium;
};

struct GiBsdfSample
{
    float3 direction;
    float3 weight;
    float pdf;
    uint delta;
    uint transmission;
};

uint GiHash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    return value ^ (value >> 16);
}

float GiRandom(inout uint state)
{
    state = state * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    word = (word >> 22u) ^ word;
    return float(word >> 8u) * (1.0 / 16777216.0);
}

float GiLuminance(float3 value)
{
    return dot(value, float3(0.2126, 0.7152, 0.0722));
}

void GiBasis(float3 normal, out float3 tangent, out float3 bitangent)
{
    float3 axis = abs(normal.z) < 0.999 ? float3(0, 0, 1) : float3(0, 1, 0);
    tangent = normalize(cross(axis, normal));
    bitangent = cross(normal, tangent);
}

float3 GiToWorld(float3 local, float3 normal)
{
    float3 tangent, bitangent;
    GiBasis(normal, tangent, bitangent);
    return local.x * tangent + local.y * bitangent + local.z * normal;
}

float3 GiCosineHemisphere(float3 normal, inout uint rng)
{
    float radius = sqrt(GiRandom(rng));
    float phi = 2.0 * GI_PI * GiRandom(rng);
    return GiToWorld(float3(radius * cos(phi), radius * sin(phi), sqrt(max(0.0, 1.0 - radius * radius))), normal);
}

float GiDielectricFresnel(float cosine, float etaI, float etaT)
{
    cosine = saturate(abs(cosine));
    float eta = etaI / etaT;
    float sinTSquared = eta * eta * max(0.0, 1.0 - cosine * cosine);
    if (sinTSquared >= 1.0) return 1.0;
    float cosT = sqrt(max(0.0, 1.0 - sinTSquared));
    float parallel = (etaT * cosine - etaI * cosT) / max(1e-7, etaT * cosine + etaI * cosT);
    float perpendicular = (etaI * cosine - etaT * cosT) / max(1e-7, etaI * cosine + etaT * cosT);
    return 0.5 * (parallel * parallel + perpendicular * perpendicular);
}

float GiDielectricF0(GiBsdf bsdf)
{
    float ratio = (bsdf.etaT - bsdf.etaI) / (bsdf.etaT + bsdf.etaI);
    return ratio * ratio;
}

float3 GiDiffuseReflectance(GiBsdf bsdf)
{
    return bsdf.transparent != 0u ? 0.0 : bsdf.baseColor * ((1.0 - bsdf.metallic) * (1.0 - GiDielectricF0(bsdf)));
}

float3 GiOpaqueFresnel(GiBsdf bsdf, float cosine)
{
    float3 f0 = lerp(GiDielectricF0(bsdf).xxx, bsdf.baseColor, bsdf.metallic);
    float fifth = pow(1.0 - saturate(cosine), 5.0);
    return f0 + (1.0 - f0) * fifth;
}

float GiSpecularProbability(GiBsdf bsdf)
{
    float diffuse = GiLuminance(GiDiffuseReflectance(bsdf));
    float specular = GiLuminance(lerp(GiDielectricF0(bsdf).xxx, bsdf.baseColor, bsdf.metallic));
    if (diffuse <= 0.0) return 1.0;
    // Even zero-F0 metals can reflect at grazing angles (Schlick Fresnel).
    return clamp(specular / max(1e-7, specular + diffuse), 0.05, 0.95);
}

bool GiIsDelta(GiBsdf bsdf)
{
    return bsdf.roughness <= 0.001 || (bsdf.transparent != 0u && abs(bsdf.etaT - bsdf.etaI) < 1e-5);
}

float GiGgxD(float cosine, float alpha)
{
    float a2 = alpha * alpha;
    float cosineSquared = cosine * cosine;
    float denominator = max(0.0, 1.0 - cosineSquared) + a2 * cosineSquared;
    return a2 / max(1e-30, GI_PI * denominator * denominator);
}

float GiGgxLambda(float cosine, float alpha)
{
    float cosineSquared = cosine * cosine;
    return 0.5 * (sqrt(1.0 + alpha * alpha * max(0.0, 1.0 - cosineSquared) / max(1e-14, cosineSquared)) - 1.0);
}

float GiGgxG(float cosO, float cosI, float alpha)
{
    return rcp(1.0 + GiGgxLambda(cosO, alpha) + GiGgxLambda(cosI, alpha));
}

float3 GiSampleGgxNormal(float3 normal, float alpha, inout uint rng)
{
    float u = GiRandom(rng);
    float phi = 2.0 * GI_PI * GiRandom(rng);
    float cosine = sqrt((1.0 - u) / max(1e-20, 1.0 + (alpha * alpha - 1.0) * u));
    float sine = sqrt(max(0.0, 1.0 - cosine * cosine));
    return normalize(GiToWorld(float3(sine * cos(phi), sine * sin(phi), cosine), normal));
}

// Isotropic GGX with height-correlated Smith masking. Transparent transmission
// includes the radiance-transport eta squared term and the refractive Jacobian.
// See pbr-book.org/4ed/Reflection_Models/Rough_Dielectric_BSDF.
void GiEvaluate(GiBsdf bsdf, float3 normal, float3 wo, float3 wi, out float3 f, out float pdf)
{
    f = 0.0;
    pdf = 0.0;
    normal = dot(normal, wo) >= 0.0 ? normal : -normal;
    float cosO = dot(normal, wo);
    float cosI = dot(normal, wi);
    if (cosO <= 1e-7 || abs(cosI) <= 1e-7) return;

    bool reflection = cosI > 0.0;
    float pSpecular = bsdf.transparent != 0u ? 1.0 : GiSpecularProbability(bsdf);
    if (bsdf.transparent == 0u)
    {
        if (!reflection) return;
        f = GiDiffuseReflectance(bsdf) / GI_PI;
        pdf = (1.0 - pSpecular) * cosI / GI_PI;
    }
    if (GiIsDelta(bsdf)) return;

    float eta = reflection ? 1.0 : bsdf.etaT / bsdf.etaI;
    float3 halfVector = wo + wi * eta;
    if (dot(halfVector, halfVector) < 1e-16) return;
    halfVector = normalize(halfVector);
    halfVector = dot(halfVector, normal) >= 0.0 ? halfVector : -halfVector;
    float oH = dot(wo, halfVector);
    float iH = dot(wi, halfVector);
    if (oH <= 0.0 || iH * cosI <= 0.0) return;

    float alpha = max(1e-4, bsdf.roughness * bsdf.roughness);
    float d = GiGgxD(saturate(dot(normal, halfVector)), alpha);
    float g = GiGgxG(cosO, abs(cosI), alpha);
    float pdfHalf = d * saturate(dot(normal, halfVector));
    if (reflection)
    {
        float dielectricF = GiDielectricFresnel(oH, bsdf.etaI, bsdf.etaT);
        float3 fresnel = bsdf.transparent != 0u ? dielectricF.xxx : GiOpaqueFresnel(bsdf, oH);
        f += fresnel * (d * g / max(1e-20, 4.0 * cosO * cosI));
        float selection = bsdf.transparent != 0u ? dielectricF : pSpecular;
        pdf += selection * pdfHalf / max(1e-20, 4.0 * oH);
    }
    else
    {
        float transmission = 1.0 - GiDielectricFresnel(oH, bsdf.etaI, bsdf.etaT);
        float denominator = iH + oH / eta;
        float denominatorSquared = denominator * denominator;
        float jacobian = abs(iH) / max(1e-30, denominatorSquared);
        pdf = transmission * pdfHalf * jacobian;
        f = (transmission * d * g * abs(iH * oH) / max(1e-30, abs(cosI * cosO) * denominatorSquared * eta * eta)).xxx;
    }
}

GiBsdfSample GiSample(GiBsdf bsdf, float3 normal, float3 wo, inout uint rng)
{
    GiBsdfSample sample = (GiBsdfSample)0;
    normal = dot(normal, wo) >= 0.0 ? normal : -normal;
    if (dot(normal, wo) <= 1e-7) return sample;

    if (bsdf.transparent != 0u && abs(bsdf.etaI - bsdf.etaT) < 1e-5)
    {
        sample.direction = -wo;
        sample.weight = 1.0;
        sample.pdf = 1.0;
        sample.delta = 1u;
        sample.transmission = 1u;
        return sample;
    }

    bool smooth = GiIsDelta(bsdf);
    float pSpecular = bsdf.transparent != 0u ? 1.0 : GiSpecularProbability(bsdf);
    bool specular = bsdf.transparent != 0u || GiRandom(rng) < pSpecular;
    if (!specular)
    {
        sample.direction = GiCosineHemisphere(normal, rng);
    }
    else
    {
        float3 halfVector = smooth ? normal : GiSampleGgxNormal(normal, max(1e-4, bsdf.roughness * bsdf.roughness), rng);
        float oH = dot(wo, halfVector);
        if (oH <= 0.0) return sample;
        if (bsdf.transparent != 0u)
        {
            float fresnel = GiDielectricFresnel(oH, bsdf.etaI, bsdf.etaT);
            if (GiRandom(rng) < fresnel)
            {
                sample.direction = reflect(-wo, halfVector);
                if (dot(sample.direction, normal) <= 0.0) return (GiBsdfSample)0;
                if (smooth)
                {
                    sample.weight = 1.0;
                    sample.pdf = fresnel;
                    sample.delta = 1u;
                    return sample;
                }
            }
            else
            {
                sample.direction = refract(-wo, halfVector, bsdf.etaI / bsdf.etaT);
                if (dot(sample.direction, normal) >= 0.0 || dot(sample.direction, sample.direction) < 0.5) return (GiBsdfSample)0;
                sample.transmission = 1u;
                if (smooth)
                {
                    float eta = bsdf.etaI / bsdf.etaT;
                    sample.weight = (eta * eta).xxx;
                    sample.pdf = 1.0 - fresnel;
                    sample.delta = 1u;
                    return sample;
                }
            }
        }
        else
        {
            sample.direction = reflect(-wo, halfVector);
            if (dot(sample.direction, normal) <= 0.0) return (GiBsdfSample)0;
            if (smooth)
            {
                sample.weight = GiOpaqueFresnel(bsdf, oH) / pSpecular;
                sample.pdf = pSpecular;
                sample.delta = 1u;
                return sample;
            }
        }
    }

    float3 f;
    GiEvaluate(bsdf, normal, wo, sample.direction, f, sample.pdf);
    if (sample.pdf <= 0.0) return (GiBsdfSample)0;
    sample.weight = f * (abs(dot(normal, sample.direction)) / sample.pdf);
    if (any(isnan(sample.weight)) || any(isinf(sample.weight))) return (GiBsdfSample)0;
    return sample;
}

uint GiNextMedium(GiBsdf bsdf, GiBsdfSample sample, uint currentMedium)
{
    return sample.transmission != 0u ? bsdf.destinationMedium : currentMedium;
}

#endif
