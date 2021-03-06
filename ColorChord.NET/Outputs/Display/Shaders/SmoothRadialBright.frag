﻿#version 330 core

#define BIN_QTY 12
#define SQRT2PI 2.506628253

out vec4 FragColor;

uniform float Amplitudes[BIN_QTY];
uniform float Means[BIN_QTY];
uniform vec2 Resolution;

uniform float Sigma;
uniform float BaseBright;

vec3 HSVToRGB(vec3 c)
{
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

vec3 AngleToRGB(float angle, float val)
{
    float Hue;
    Hue = (1.0 - step(4.0 / 12.0, angle)) * ((1.0 / 3.0) - angle) * 0.5; // Yellow -> Red
    Hue += (step(4.0 / 12.0, angle) - step(8.0 / 12.0, angle)) * (1 - (angle - (1.0 / 3.0))); // Red -> Blue
    Hue += step(8.0 / 12.0, angle) * ((2.0 / 3.0) - (1.5 * (angle - (2.0 / 3.0)))); // Blue -> Yellow
    return HSVToRGB(vec3(Hue, 1.0, val));
}

void main()
{
    vec2 Coords = ((gl_FragCoord.xy / Resolution) * 2.0) - vec2(1.0);
    float Angle = (atan(-Coords.x, -Coords.y) + 3.1415926535) / 6.2831853071795864;
    float Radius = distance(vec2(0.0), Coords);
    
    float Value = 0.0;

    for (int i = 0; i < BIN_QTY; i++)
    {
        float x = Means[i] - (Angle * BIN_QTY);
        x += BIN_QTY * (1 - step(BIN_QTY / -2.0, x));
        x -= BIN_QTY * step(BIN_QTY / 2.0, x);
        Value += Amplitudes[i] / (Sigma * SQRT2PI) * exp(-(x * x) / (2 * Sigma * Sigma));
    }

    float OnePixelDist = 2.0 / Resolution.x;

    FragColor = vec4(AngleToRGB(Angle, (BaseBright + (Value * (1.0 - BaseBright))) * (1 - smoothstep(0.98, 0.98 + OnePixelDist, Radius))), 1.0);
}