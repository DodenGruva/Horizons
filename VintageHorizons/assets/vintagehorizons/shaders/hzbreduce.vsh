#version 330 core

// One full-screen triangle built from gl_VertexID alone: no vertex buffer, no
// attributes, no element array. The reduction pass has nothing to say about
// geometry, and a triangle that covers the target avoids the seam a two-triangle
// quad puts down its own diagonal.
//
// Depth is written by the fragment stage through gl_FragDepth, so the position
// this stage emits has no bearing on what lands in the pyramid.
void main()
{
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}
