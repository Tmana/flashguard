namespace FlashGuard.Capture;

static class Shaders
{
    // Textures are sampled through _SRGB views and the history is FP16, so all math is in linear light,
    // which is what flash thresholds (and photosensitivity) are defined in.
    public const string Source = """
        Texture2D<float4> Cur  : register(t0);
        Texture2D<float4> Prev : register(t1);

        cbuffer Params : register(b0)
        {
            float MaxDelta;  // per-channel linear change allowed this frame
            float Dim;       // extra darkening (smooth) or overlay alpha (dim)
            float Opacity;   // fade in/out of the whole overlay
            float Pad;
        };

        float4 VS(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        // Slew-rate limiter: each pixel may only move MaxDelta toward the live desktop per frame.
        float4 PSFilter(float4 pos : SV_Position) : SV_Target
        {
            int3 p = int3(pos.xy, 0);
            float3 c = Cur.Load(p).rgb;
            float3 h = Prev.Load(p).rgb;
            return float4(h + clamp(c - h, -MaxDelta, MaxDelta), 1);
        }

        float4 PSPresentSmooth(float4 pos : SV_Position) : SV_Target
        {
            float3 c = Prev.Load(int3(pos.xy, 0)).rgb * (1 - Dim);
            return float4(c * Opacity, Opacity);
        }

        float4 PSPresentDim(float4 pos : SV_Position) : SV_Target
        {
            return float4(0, 0, 0, Dim * Opacity);
        }
        """;
}
