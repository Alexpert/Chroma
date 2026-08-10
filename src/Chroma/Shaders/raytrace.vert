// Vertex stage for a fullscreen quad. There is no transform here at all: the positions
// arrive in clip space and are passed straight through. The camera is applied per pixel,
// in the fragment stage, when the primary ray is built.
#version 330 core

layout (location = 0) in vec2 aPosition;

// Normalised device coordinates, -1..1, interpolated across the quad. This is what the
// fragment stage turns into a ray direction.
out vec2 vNdc;

void main()
{
    vNdc = aPosition;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
