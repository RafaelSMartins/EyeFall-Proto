// Custom Function (File) for the HandsLit Shader Graph.
// Lambert (wrapped / half-lambert) direct light + SH ambient, main light shadows and additional
// lights (Forward and Forward+ cluster loop). The hands quad sits right in front of the camera, so
// lighting is evaluated at "arm length" along the view ray (ProbeDepth) with a stylised normal
// that faces the camera and tilts up (NormalUp).
// Needs "Keep Lighting Variants" enabled on the Unlit target (includes Lighting.hlsl + shadow keywords).
#ifndef EYEFALL_HANDS_LIGHTING_INCLUDED
#define EYEFALL_HANDS_LIGHTING_INCLUDED

float EyeFallWrapLambert(float3 n, float3 l, float wrap)
{
    return saturate((dot(n, l) + wrap) / (1.0 + wrap));
}

void HandsLighting_float(float3 Albedo, float3 PositionWS, float4 ScreenPosition, float Wrap, float NormalUp,
                         float ProbeDepth, float MinLight, float Influence, out float3 Color)
{
#ifdef SHADERGRAPH_PREVIEW
    Color = Albedo;
#else
    float3 viewDir = normalize(PositionWS - _WorldSpaceCameraPos);
    float3 lightPosWS = _WorldSpaceCameraPos + viewDir * ProbeDepth;
    float3 n = normalize(-viewDir + float3(0, NormalUp, 0));

    // Main light: Lambert * shadow.
    Light mainLight = GetMainLight(TransformWorldToShadowCoord(lightPosWS));
    float3 direct = mainLight.color * EyeFallWrapLambert(n, mainLight.direction, Wrap)
                  * mainLight.shadowAttenuation * mainLight.distanceAttenuation;

    // Additional (point / spot) lights.
    #if defined(_ADDITIONAL_LIGHTS) || defined(_CLUSTER_LIGHT_LOOP)
        uint lightCount = 0;
        #if defined(_ADDITIONAL_LIGHTS)
            lightCount = GetAdditionalLightsCount();
        #endif
        #if USE_CLUSTER_LIGHT_LOOP
            // LIGHT_LOOP_BEGIN (Forward+) reads inputData.normalizedScreenSpaceUV and inputData.positionWS.
            InputData inputData = (InputData)0;
            inputData.normalizedScreenSpaceUV = ScreenPosition.xy;
            inputData.positionWS = lightPosWS;
        #endif
        LIGHT_LOOP_BEGIN(lightCount)
            #if !USE_CLUSTER_LIGHT_LOOP
                lightIndex = GetPerObjectLightIndex(lightIndex);
            #endif
            Light light = GetAdditionalPerObjectLight(lightIndex, lightPosWS);
            direct += light.color * EyeFallWrapLambert(n, light.direction, Wrap) * light.distanceAttenuation;
        LIGHT_LOOP_END
    #endif

    // Ambient approximates indirect light and is added to the direct term.
    float3 ambient = SampleSH(n);
    float3 lit = Albedo * max(direct + ambient, MinLight);
    Color = lerp(Albedo, lit, Influence);
#endif
}

void HandsLighting_half(half3 Albedo, float3 PositionWS, float4 ScreenPosition, half Wrap, half NormalUp,
                        half ProbeDepth, half MinLight, half Influence, out half3 Color)
{
    float3 c;
    HandsLighting_float(Albedo, PositionWS, ScreenPosition, Wrap, NormalUp, ProbeDepth, MinLight, Influence, c);
    Color = c;
}

#endif
