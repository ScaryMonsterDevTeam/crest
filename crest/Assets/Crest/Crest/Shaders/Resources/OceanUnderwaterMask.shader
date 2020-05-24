// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

Shader "Crest/Underwater/Ocean Mask"
{
	SubShader
	{
		Pass
		{
			// We always disable culling when rendering ocean mask, as we only
			// use it for underwater rendering features.
			Cull Off

			CGPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			// for VFACE
			#pragma target 3.0

			#include "UnityCG.cginc"
			#include "Lighting.cginc"

			struct Attributes
			{
				// The old unity macros require this name and type.
				float4 vertex : POSITION;

				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float4 screenPos : TEXCOORD1;

				UNITY_VERTEX_OUTPUT_STEREO
			};


			#include "../OceanConstants.hlsl"
			#include "../OceanInputsDriven.hlsl"
			#include "../OceanGlobals.hlsl"
			#include "../OceanLODData.hlsl"
			#include "../OceanHelpersNew.hlsl"
			#include "../OceanHelpers.hlsl"

			// Hack - due to SV_IsFrontFace occasionally coming through as true for backfaces,
			// add a param here that forces ocean to be in undrwater state. I think the root
			// cause here might be imprecision or numerical issues at ocean tile boundaries, although
			// i'm not sure why cracks are not visible in this case.
			float _ForceUnderwater;
			sampler2D _CrestOceanOccluderMaskTexture;
			sampler2D _CrestOceanOccluderMaskDepthTexture;

			Varyings Vert(Attributes v)
			{
				Varyings output;


				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_OUTPUT(Varyings, output);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

				float3 worldPos = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0));

				// Vertex snapping and lod transition
				float lodAlpha;
				SnapAndTransitionVertLayout(_InstanceData.x, worldPos, lodAlpha);

				// Calculate sample weights. params.z allows shape to be faded out (used on last lod to support pop-less scale transitions)
				const float wt_smallerLod = (1.0 - lodAlpha) * _LD_Params[_LD_SliceIndex].z;
				const float wt_biggerLod = (1.0 - wt_smallerLod) * _LD_Params[_LD_SliceIndex + 1].z;
				// Sample displacement textures, add results to current world pos / normal / foam
				const float2 positionWS_XZ_before = worldPos.xz;

				// Data that needs to be sampled at the undisplaced position
				if (wt_smallerLod > 0.001)
				{
					half sss = 0.0;
					const float3 uv_slice_smallerLod = WorldToUV(positionWS_XZ_before);
					SampleDisplacements(_LD_TexArray_AnimatedWaves, uv_slice_smallerLod, wt_smallerLod, worldPos, sss);
				}
				if (wt_biggerLod > 0.001)
				{
					half sss = 0.0;
					const float3 uv_slice_biggerLod = WorldToUV_BiggerLod(positionWS_XZ_before);
					SampleDisplacements(_LD_TexArray_AnimatedWaves, uv_slice_biggerLod, wt_biggerLod, worldPos, sss);
				}

				output.positionCS = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
				output.screenPos =  ComputeScreenPos(output.positionCS);
				return output;
			}

			half4 Frag(const Varyings input, const float facing : VFACE) : SV_Target
			{
				half3 uv_z = input.screenPos.xyz/input.screenPos.w;

				float overrideMask = tex2D(_CrestOceanOccluderMaskTexture, uv_z.xy).x;
				float overrideDepth = tex2D(_CrestOceanOccluderMaskDepthTexture, uv_z.xy).x;

				// TODO(TRC):Now @optimisation pack opaque information in the underwater mask texture, only enable
				// parts of this based on which underwater features we have enabled.
				if(overrideMask == OVERRIDE_MASK_UNDERWATER_DISABLE || (overrideMask == OVERRIDE_MASK_UNDERWATER_DISABLE_FRONT && overrideDepth < uv_z.z))
				{
					discard;
				}

				if(IsUnderwater(facing, _ForceUnderwater))
				{
					return (half4)UNDERWATER_MASK_WATER_SURFACE_BELOW;
				}
				else
				{
					return (half4)UNDERWATER_MASK_WATER_SURFACE_ABOVE;
				}
			}
			ENDCG
		}
	}
}
