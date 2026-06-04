// sy_fullscreen.metal
//
// Built-in preamble for every SurfaceEffect fragment shader (sy_effect_create). It is prepended
// to the caller's fragment source before runtime compilation, so a caller fragment may use the
// `VOut` stage-in struct (with `uv` in [0,1]) and the linear clamped sampler `sy_samp`.
//
// The vertex stage emits a single oversized triangle covering the viewport - no vertex buffer.

#include <metal_stdlib>
using namespace metal;

struct VOut {
    float4 pos [[position]];
    float2 uv;
};

vertex VOut sy_fullscreen_vs(uint vid [[vertex_id]]) {
    float2 uv = float2(float((vid << 1) & 2), float(vid & 2));
    VOut o;
    o.uv = uv;
    o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

constexpr sampler sy_samp(coord::normalized, address::clamp_to_edge, filter::linear);
