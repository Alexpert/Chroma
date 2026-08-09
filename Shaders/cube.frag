// Fragment stage -- this is the main editing surface. Change it, re-run, see the result.
#version 330 core

in vec3 vNormal;

out vec4 FragColor;

void main()
{
    // Remap the normal from [-1, 1] to [0, 1] so each face gets a distinct colour.
    // Replace this with whatever you want to experiment with.
    FragColor = vec4(normalize(vNormal) * 0.5 + 0.5, 1.0);
}
