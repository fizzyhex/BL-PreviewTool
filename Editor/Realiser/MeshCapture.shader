// Streams a mesh's GPU-only vertex data out through a UAV, so non-readable bundle meshes can be realised.
// Each vertex shader invocation writes its fully-decoded attributes to _Out[SV_VertexID + _BaseVertex].
Shader "Hidden/BLPTool/MeshCapture"
{
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            struct appdata
            {
                float3 pos : POSITION;
                float3 nrm : NORMAL;
                float4 tan : TANGENT;
                float4 col : COLOR;
                float4 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
                float4 uv2 : TEXCOORD2;
                float4 uv3 : TEXCOORD3;
                float4 uv4 : TEXCOORD4;
                float4 uv5 : TEXCOORD5;
                float4 uv6 : TEXCOORD6;
                float4 uv7 : TEXCOORD7;
                uint vid : SV_VertexID;
            };

            struct CapturedVertex
            {
                float3 pos;
                float3 nrm;
                float4 tan;
                float4 col;
                float4 uv[8];
            };

            RWStructuredBuffer<CapturedVertex> _Out : register(u1);
            int _BaseVertex;

            float4 vert(appdata v) : SV_POSITION
            {
                CapturedVertex o;
                o.pos = v.pos;
                o.nrm = v.nrm;
                o.tan = v.tan;
                o.col = v.col;
                o.uv[0] = v.uv0; o.uv[1] = v.uv1; o.uv[2] = v.uv2; o.uv[3] = v.uv3;
                o.uv[4] = v.uv4; o.uv[5] = v.uv5; o.uv[6] = v.uv6; o.uv[7] = v.uv7;
                _Out[v.vid + (uint)_BaseVertex] = o;
                return float4(0, 0, -2, 1); // outside the clip volume; nothing rasterises
            }

            float4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
