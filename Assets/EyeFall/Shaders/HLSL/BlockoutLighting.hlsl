// Custom Function (File) nodes for the Blockout Shader Graphs (BlockoutGrid, BlockoutBoard).
// Custom lighting in an Unlit graph, as in The Unity Shaders Bible ch. 2:
//   MainLight     -> direction, colour and shadow attenuation of the URP main light
//   CustomLambert -> max(0, N·L) * LightIntensity * BaseColor
//   CustomAmbient -> hemispheric ambient (Ground -> Equator -> Sky along Normal.y) * BaseColor
// Needs "Keep Lighting Variants" on the Unlit target (Lighting.hlsl + main light shadow keywords).
#ifndef EYEFALL_BLOCKOUT_LIGHTING_INCLUDED
#define EYEFALL_BLOCKOUT_LIGHTING_INCLUDED

void MainLight_float(float3 PositionWS, out float3 Direction, out float3 Color, out float ShadowAtten)
{
#ifdef SHADERGRAPH_PREVIEW
    Direction = normalize(float3(0.5, 0.8, 0.3));
    Color = float3(1.0, 1.0, 1.0);
    ShadowAtten = 1.0;
#else
    Light light = GetMainLight(TransformWorldToShadowCoord(PositionWS));
    Direction = light.direction;
    Color = light.color * light.distanceAttenuation;
    ShadowAtten = light.shadowAttenuation;
#endif
}

void CustomLambert_float(float3 Normal, float3 LightDirection, float3 LightIntensity, float3 BaseColor, out float3 Out)
{
    float NdotL = max(0.0, dot(normalize(Normal), LightDirection));
    Out = NdotL * LightIntensity * BaseColor;
}

void CustomAmbient_float(float3 BaseColor, float3 Sky, float3 Equator, float3 Ground, float3 Normal, out float3 Out)
{
    float t = normalize(Normal).y * 0.5 + 0.5;
    float t_AB = saturate(t * 2.0);
    float t_BC = saturate((t - 0.5) * 2.0);

    float3 color_AB = lerp(Ground, Equator, t_AB);
    float3 color_BC = lerp(color_AB, Sky, t_BC);

    Out = color_BC * BaseColor;
}

#endif
