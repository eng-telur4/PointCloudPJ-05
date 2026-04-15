Shader "Custom/GPUPointCloud"
{
    Properties {
        _BaseSize ("Base Size", Float) = 0.2
        _SizeScale ("Size Scale", Float) = 1.0
        _Color ("Tint", Color) = (1,1,1,1)
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        Pass {
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _PointBuffer; // xyz: world pos, w: size (multiplier)
            StructuredBuffer<float4> _ColorBuffer; // rgba
            float _BaseSize;
            float _SizeScale;
            float4 _Color;

            struct appdata {
                float3 vertex : POSITION;   // quad vertex, expected centered (-0.5..0.5)
                float2 uv     : TEXCOORD0;  // 0..1 for circle mask
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float4 color : COLOR;
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                UNITY_SETUP_INSTANCE_ID(v);

                // point data
                float4 pd = _PointBuffer[instanceID];
                float3 worldPos = pd.xyz;
                float sizeMul  = pd.w;

                // camera-facing billboard basis
                float3 toCam = normalize(_WorldSpaceCameraPos - worldPos);
                float3 worldUp = float3(0,1,0);
                // handle case where toCam is colinear with up
                if (abs(dot(toCam, worldUp)) > 0.999) worldUp = float3(1,0,0);
                float3 right = normalize(cross(worldUp, toCam));
                float3 up    = normalize(cross(toCam, right));

                // distance-based scaling (camera distance)
                float dist = max(length(_WorldSpaceCameraPos - worldPos), 1e-6);
                float scaledSize = _BaseSize * sizeMul * (_SizeScale / dist);

                // quad vertex is expected e.g. (-0.5,-0.5,0) .. (0.5,0.5,0)
                float3 offset = right * v.vertex.x * scaledSize + up * v.vertex.y * scaledSize;
                float3 finalPos = worldPos + offset;

                v2f o;
                o.pos = UnityWorldToClipPos(finalPos);
                o.uv  = v.uv;
                o.color = _ColorBuffer[instanceID] * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 円形にする（UVの中心が (0.5,0.5)）
                float2 pc = i.uv * 2.0 - 1.0;
                if (dot(pc, pc) > 1.0) discard;
                return i.color;
            }
            ENDCG
        }
    }
}