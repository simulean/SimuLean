// Floor-plan underlay (FloorPlanUnderlay component, docs/architecture.md §4.7.2): one unlit, alpha-blended quad on the
// floor. The texture is single-channel (R = line coverage x group intensity, mipmapped); lines are drawn in _Color with
// alpha = R x _Opacity, so the plan stays faint under the model. Built-in render pipeline.
Shader "SimuLean/FloorPlanUnderlay"
{
    Properties
    {
        _MainTex ("Plan (R = line coverage)", 2D) = "black" {}
        _Color ("Line colour", Color) = (0.13, 0.16, 0.22, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.6
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Offset -1, -1
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            half _Opacity;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; UNITY_FOG_COORDS(1) };
            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = _Color;
                c.a = saturate(tex2D(_MainTex, i.uv).r * _Opacity);
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
