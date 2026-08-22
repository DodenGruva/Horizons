#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// The indirect variant of lodterrain. Same source, and the define is the only
// difference: it switches the per-section values from uniforms to instanced
// attributes, because a multi-draw has no per-draw uniforms. Nothing else about
// the shader changes, which is what makes "draws identical pixels" a gate this
// path can actually be held to rather than a source review.
#define VH_INDIRECT 1
#include lodterrainbody.fsh
