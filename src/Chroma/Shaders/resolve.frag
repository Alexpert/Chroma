// Second pass: turn accumulated radiance into pixels.
//
// The accumulation buffer holds linear radiance, which is unbounded -- a light is not
// limited to 1.0 -- while a display expects 0..1 in a non-linear space. Skipping either step
// below makes a perfectly correct render look like a lighting bug: without tone mapping
// everything bright clips to flat white, and without gamma the whole image reads far too
// dark, which invites "fixing" it by raising the light intensity.
#version 330 core

in vec2 vNdc;
out vec4 FragColor;

uniform sampler2D uAccumulation;
uniform float     uExposure;

// Narkowicz's five-constant fit of the ACES filmic curve. It maps [0, inf) into [0, 1]
// with a shoulder, so highlights roll off instead of posterising the way a clamp does.
vec3 acesFilmic(vec3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;

    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
    // The vertex stage hands over clip space; the texture wants 0..1.
    vec2 uv = vNdc * 0.5 + 0.5;

    vec3 color = texture(uAccumulation, uv).rgb * uExposure;

    color = acesFilmic(color);
    color = pow(color, vec3(1.0 / 2.2));   // linear -> sRGB-ish

    FragColor = vec4(color, 1.0);
}
