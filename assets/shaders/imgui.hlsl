// ImGui renderer shaders. The embedded *.cso bytecode is generated from this file with
// `dotnet run -c Release -p:EnableAgentSelfTests=true -- --dump-shaders assets/shaders`;
// `--ui-smoke` fails when the embedded bytecode no longer matches this source.
cbuffer Display : register(b0) { float2 displayPos; float2 displaySize; };
Texture2D image : register(t0);
SamplerState imageSampler : register(s0);
struct VertexInput { float2 position : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
struct PixelInput { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };

PixelInput VertexMain(VertexInput input)
{
    PixelInput output;
    output.position = float4((input.position - displayPos) / displaySize * float2(2, -2) + float2(-1, 1), 0, 1);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}

float4 PixelMain(PixelInput input) : SV_Target { return input.color * image.Sample(imageSampler, input.uv); }

// The font atlas stores coverage only: white, with alpha from the red channel.
float4 AlphaPixelMain(PixelInput input) : SV_Target
{
    return input.color * float4(1, 1, 1, image.Sample(imageSampler, input.uv).r);
}
