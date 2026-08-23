#version 330 core

// One level of the hierarchical depth pyramid: each texel takes the FARTHEST of
// the texels it stands for in the level above it.
//
// Farthest, not nearest and not the average, and that single choice is what makes
// the pyramid safe to test against. A texel that reported anything nearer than the
// true farthest sample would let the test conclude "this terrain is behind the
// scene" about ground that is in fact visible through a gap in it, and terrain
// would disappear. Rounding to the far side can only ever fail to hide something
// that was hidden, which costs a draw call and nothing else.
//
// This is the conventional depth convention: 0 at the near plane, 1 at the far
// plane, so farthest is max(). The C# side refuses to build a pyramid at all if
// the engine is ever found running reversed depth, rather than silently inverting.

// Sizes arrive as four scalar ints, never as two ivec2. The engine's Vec2i uniform
// overload reaches glUniform2f, which an integer uniform rejects outright with
// GL_INVALID_OPERATION - leaving the value silently at zero. That cost a whole
// session once already; see gotcha G42.
uniform sampler2D sourceDepth;
uniform int sourceWidth;
uniform int sourceHeight;
uniform int targetWidth;
uniform int targetHeight;

float At(ivec2 texel)
{
    ivec2 limit = ivec2(sourceWidth - 1, sourceHeight - 1);
    return texelFetch(sourceDepth, clamp(texel, ivec2(0), limit), 0).r;
}

void main()
{
    ivec2 target = ivec2(gl_FragCoord.xy);
    ivec2 source = target * 2;

    float farthest = At(source);
    farthest = max(farthest, At(source + ivec2(1, 0)));
    farthest = max(farthest, At(source + ivec2(0, 1)));
    farthest = max(farthest, At(source + ivec2(1, 1)));

    // An odd source dimension halves to a level with one fewer texel than it needs,
    // so a plain 2x2 fold drops the final row or column entirely. A dropped texel is
    // a depth this level never learned about, and the pyramid stops being
    // conservative exactly where geometry runs off the edge of the screen. The last
    // texel of the target absorbs the leftover instead.
    bool oddX = (sourceWidth & 1) != 0 && target.x == targetWidth - 1;
    bool oddY = (sourceHeight & 1) != 0 && target.y == targetHeight - 1;

    if (oddX)
    {
        farthest = max(farthest, At(ivec2(sourceWidth - 1, source.y)));
        farthest = max(farthest, At(ivec2(sourceWidth - 1, source.y + 1)));
    }
    if (oddY)
    {
        farthest = max(farthest, At(ivec2(source.x, sourceHeight - 1)));
        farthest = max(farthest, At(ivec2(source.x + 1, sourceHeight - 1)));
    }
    if (oddX && oddY)
    {
        farthest = max(farthest, At(ivec2(sourceWidth - 1, sourceHeight - 1)));
    }

    gl_FragDepth = farthest;
}
