Shader "Custom/DashedSurfaceVertexColor"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0, 0, 1)
        [Enum(UnityEngine.Rendering.CompareFunction)] _DepthTest ("Depth test", Float) = 8
        [Toggle] _FrontOnly ("Front faces only", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" }
        Pass
        {
            Cull Back
            ZWrite Off
            ZTest [_DepthTest]
            Blend SrcAlpha OneMinusSrcAlpha
            Offset -1, -1

            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _FrontOnly;

            struct AppData
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float3 normal : NORMAL;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                fixed4 color : COLOR;
                float3 worldPosition : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            Varyings Vert(AppData input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.color = input.color * _Color;
                output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                if (_FrontOnly > 0.5)
                    clip(dot(normalize(input.worldNormal),
                        normalize(_WorldSpaceCameraPos.xyz - input.worldPosition)));
                return input.color;
            }
            ENDCG
        }
    }
    Fallback Off
}
