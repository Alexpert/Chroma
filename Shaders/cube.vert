// Vertex stage -- edit this file freely, it is reloaded on every run of the program.
#version 330 core

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec3 vNormal;

void main()
{
    // Uniform normal matrix would be needed for non-uniform scaling; the model
    // transform here is a pure rotation, so passing the normal through is enough.
    vNormal = mat3(uModel) * aNormal;

    gl_Position = uProjection * uView * uModel * vec4(aPosition, 1.0);
}
