// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

namespace Crest
{
    using Unity.Collections;
    using UnityEngine;
    using UnityEngine.Rendering;

    public partial class UnderwaterRenderer
    {
        const string k_ShaderPathUnderwaterEffect = "Hidden/Crest/Underwater/Underwater Effect";
        internal const string k_KeywordFullScreenEffect = "_FULL_SCREEN_EFFECT";
        internal const string k_KeywordDebugViewOceanMask = "_DEBUG_VIEW_OCEAN_MASK";

        internal static readonly int sp_CrestCameraColorTexture = Shader.PropertyToID("_CrestCameraColorTexture");
        static readonly int sp_CrestBoundaryStencil = Shader.PropertyToID("_CrestBoundaryStencil");
        static readonly int sp_InvViewProjection = Shader.PropertyToID("_InvViewProjection");
        static readonly int sp_InvViewProjectionRight = Shader.PropertyToID("_InvViewProjectionRight");
        static readonly int sp_AmbientLighting = Shader.PropertyToID("_AmbientLighting");
        static readonly int sp_HorizonNormal = Shader.PropertyToID("_HorizonNormal");
        static readonly int sp_DataSliceOffset = Shader.PropertyToID("_DataSliceOffset");

        CommandBuffer _underwaterEffectCommandBuffer;
        PropertyWrapperMaterial _underwaterEffectMaterial;
        internal readonly UnderwaterSphericalHarmonicsData _sphericalHarmonicsData = new UnderwaterSphericalHarmonicsData();

        Material _depthCopyMaterial;

        RenderTargetIdentifier _colorTarget = new RenderTargetIdentifier
        (
            BuiltinRenderTextureType.CameraTarget,
            0,
            CubemapFace.Unknown,
            -1
        );
        RenderTargetIdentifier _stencilTarget = new RenderTargetIdentifier
        (
            sp_CrestBoundaryStencil,
            0,
            CubemapFace.Unknown,
            -1
        );

        internal class UnderwaterSphericalHarmonicsData
        {
            internal Color[] _ambientLighting = new Color[1];
            internal Vector3[] _shDirections = { new Vector3(0.0f, 0.0f, 0.0f) };
        }

        void SetupUnderwaterEffect()
        {
            if (_underwaterEffectMaterial?.material == null)
            {
                _underwaterEffectMaterial = new PropertyWrapperMaterial(k_ShaderPathUnderwaterEffect);
            }

            if (_underwaterEffectCommandBuffer == null)
            {
                _underwaterEffectCommandBuffer = new CommandBuffer()
                {
                    name = "Underwater Pass",
                };
            }

            if (_depthCopyMaterial == null)
            {
                _depthCopyMaterial = new Material(Shader.Find("Hidden/Crest/Helpers/DepthCopy"));
            }
        }

        void OnDisableUnderwaterEffect()
        {
            DisableUnderwaterEffectKeywords(_underwaterEffectMaterial.material);
        }

        static void DisableUnderwaterEffectKeywords(Material material)
        {
            // Multiple keywords from same set can be enabled at the same time leading to undefined behaviour so we need
            // to disable all keywords from a set first.
            // https://docs.unity3d.com/Manual/shader-keywords-scripts.html
            material.DisableKeyword(k_KeywordBoundary2D);
            material.DisableKeyword(k_KeywordBoundary3D);
            material.DisableKeyword(k_KeywordBoundaryVolume);
        }

        void OnPreRenderUnderwaterEffect()
        {
            // Ensure legacy underwater fog is disabled.
            if (_firstRender)
            {
                OceanRenderer.Instance.OceanMaterial.DisableKeyword("_OLD_UNDERWATER");
            }

            RenderTextureDescriptor descriptor = XRHelpers.GetRenderTextureDescriptor(_camera);
            descriptor.useDynamicScale = _camera.allowDynamicResolution;
            // Format must be correct for CopyTexture to work. Hopefully this is good enough.
            if (_camera.allowHDR)
            {
                descriptor.colorFormat = RenderTextureFormat.DefaultHDR;
            }

            var temporaryColorBuffer = RenderTexture.GetTemporary(descriptor);
            temporaryColorBuffer.name = "Crest Temporary Color";

            UpdatePostProcessMaterial(
                _camera,
                _underwaterEffectMaterial,
                _sphericalHarmonicsData,
                _meniscus,
                _firstRender || _copyOceanMaterialParamsEachFrame,
                _debug._viewOceanMask,
                _debug._viewStencil,
                _filterOceanData
            );

            // Call after UpdatePostProcessMaterial as it copies material from ocean which will overwrite this.
            SetInverseViewProjectionMatrix(_underwaterEffectMaterial.material);

            _underwaterEffectCommandBuffer.Clear();

            if (IsStencilBufferRequired)
            {
                // TODO: Check that QualitySettings.antiAliasing cannot be zero.
                descriptor.msaaSamples = _camera.allowMSAA ? QualitySettings.antiAliasing : 1;
                descriptor.colorFormat = RenderTextureFormat.Depth;
                descriptor.depthBufferBits = 24;
                descriptor.bindMS = _camera.allowMSAA;
                _underwaterEffectCommandBuffer.GetTemporaryRT(sp_CrestBoundaryStencil, descriptor);
                _underwaterEffectCommandBuffer.SetRenderTarget(temporaryColorBuffer, _stencilTarget);
                _underwaterEffectCommandBuffer.ClearRenderTarget(true, true, Color.black);
            }
            else
            {
                _underwaterEffectCommandBuffer.SetRenderTarget(temporaryColorBuffer);
                _underwaterEffectCommandBuffer.ClearRenderTarget(true, false, Color.black);
            }

            if (_camera.allowMSAA)
            {
                // Use blit if MSAA is active because transparents were not included with CopyTexture.
                // Not sure if we need an MSAA resolve? Not sure how to do that...
                _underwaterEffectCommandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, temporaryColorBuffer);
            }
            else
            {
                // Copy the frame buffer as we cannot read/write at the same time. If it causes problems, replace with Blit.
                _underwaterEffectCommandBuffer.CopyTexture(BuiltinRenderTextureType.CameraTarget, temporaryColorBuffer);
            }

            _underwaterEffectMaterial.SetTexture(sp_CrestCameraColorTexture, temporaryColorBuffer);

            if (IsStencilBufferRequired)
            {
                if (_camera.allowMSAA)
                {
                    // Blit with a depth write shader to populate the depth buffer.
                    _underwaterEffectCommandBuffer.Blit(BuiltinRenderTextureType.None, _stencilTarget, _depthCopyMaterial);
                }
                else
                {
                    _underwaterEffectCommandBuffer.CopyTexture(BuiltinRenderTextureType.Depth, _stencilTarget);
                }

                _underwaterEffectCommandBuffer.SetRenderTarget(_colorTarget, _stencilTarget);
            }
            else
            {
                _underwaterEffectCommandBuffer.SetRenderTarget(_colorTarget);
            }


            DisableUnderwaterEffectKeywords(_underwaterEffectMaterial.material);

            if (_mode == Mode.FullScreen)
            {
                _underwaterEffectCommandBuffer.DrawProcedural(Matrix4x4.identity, _underwaterEffectMaterial.material, shaderPass: 0, MeshTopology.Triangles, 3, 1);
            }
            else
            {
                _underwaterEffectMaterial.material.DisableKeyword("_FULL_SCREEN_EFFECT");
                _underwaterEffectCommandBuffer.DrawMesh(_waterVolumeBoundaryGeometry.mesh, _waterVolumeBoundaryGeometry.transform.localToWorldMatrix, _underwaterEffectMaterial.material, submeshIndex: 0, shaderPass: 1);

                if (_mode == Mode.GeometryVolume)
                {
                    _underwaterEffectCommandBuffer.DrawMesh(_waterVolumeBoundaryGeometry.mesh, _waterVolumeBoundaryGeometry.transform.localToWorldMatrix, _underwaterEffectMaterial.material, submeshIndex: 0, shaderPass: 2);
                    _underwaterEffectCommandBuffer.DrawProcedural(Matrix4x4.identity, _underwaterEffectMaterial.material, shaderPass: 3, MeshTopology.Triangles, 3, 1);
                }
            }

            RenderTexture.ReleaseTemporary(temporaryColorBuffer);
            _underwaterEffectCommandBuffer.ReleaseTemporaryRT(sp_CrestBoundaryStencil);
        }

        internal static void UpdatePostProcessMaterial(
            Camera camera,
            PropertyWrapperMaterial underwaterPostProcessMaterialWrapper,
            UnderwaterSphericalHarmonicsData sphericalHarmonicsData,
            bool isMeniscusEnabled,
            bool copyParamsFromOceanMaterial,
            bool debugViewPostProcessMask,
            bool debugViewStencil,
            int dataSliceOffset
        )
        {
            Material underwaterPostProcessMaterial = underwaterPostProcessMaterialWrapper.material;
            if (copyParamsFromOceanMaterial)
            {
                // Measured this at approx 0.05ms on dell laptop
                underwaterPostProcessMaterial.CopyPropertiesFromMaterial(OceanRenderer.Instance.OceanMaterial);
            }

            underwaterPostProcessMaterial.SetVector("_DepthFogDensity", OceanRenderer.Instance.UnderwaterDepthFogDensity);

            // Enabling/disabling keywords each frame don't seem to have large measurable overhead
            underwaterPostProcessMaterial.SetKeyword(k_KeywordDebugViewOceanMask, debugViewPostProcessMask);
            underwaterPostProcessMaterial.SetKeyword("_DEBUG_VIEW_STENCIL", debugViewStencil);
            underwaterPostProcessMaterial.SetKeyword("CREST_MENISCUS", isMeniscusEnabled);

            // We sample shadows at the camera position which will be the first slice.
            // We also use this for caustics to get the displacement.
            underwaterPostProcessMaterial.SetFloat(LodDataMgr.sp_LD_SliceIndex, 0);
            underwaterPostProcessMaterial.SetInt(sp_DataSliceOffset, dataSliceOffset);

            LodDataMgrAnimWaves.Bind(underwaterPostProcessMaterialWrapper);
            LodDataMgrSeaFloorDepth.Bind(underwaterPostProcessMaterialWrapper);
            LodDataMgrShadow.Bind(underwaterPostProcessMaterialWrapper);

            float seaLevel = OceanRenderer.Instance.SeaLevel;

            // We don't both setting the horizon value if we know we are going to be having to apply the effect
            // full-screen anyway.
            var forceFullShader = OceanRenderer.Instance.ViewerHeightAboveWater < -2f;
            if (!forceFullShader)
            {
                float maxOceanVerticalDisplacement = OceanRenderer.Instance.MaxVertDisplacement * 0.5f;
                float cameraYPosition = camera.transform.position.y;
                float nearPlaneFrustumWorldHeight;
                {
                    float current = camera.ViewportToWorldPoint(new Vector3(0f, 0f, camera.nearClipPlane)).y;
                    float maxY = current, minY = current;

                    current = camera.ViewportToWorldPoint(new Vector3(0f, 1f, camera.nearClipPlane)).y;
                    maxY = Mathf.Max(maxY, current);
                    minY = Mathf.Min(minY, current);

                    current = camera.ViewportToWorldPoint(new Vector3(1f, 0f, camera.nearClipPlane)).y;
                    maxY = Mathf.Max(maxY, current);
                    minY = Mathf.Min(minY, current);

                    current = camera.ViewportToWorldPoint(new Vector3(1f, 1f, camera.nearClipPlane)).y;
                    maxY = Mathf.Max(maxY, current);
                    minY = Mathf.Min(minY, current);

                    nearPlaneFrustumWorldHeight = maxY - minY;
                }

                forceFullShader = (cameraYPosition + nearPlaneFrustumWorldHeight + maxOceanVerticalDisplacement) <= seaLevel;
            }

            if (forceFullShader)
            {
                underwaterPostProcessMaterial.EnableKeyword(k_KeywordFullScreenEffect);
            }
            else
            {
                underwaterPostProcessMaterial.DisableKeyword(k_KeywordFullScreenEffect);
            }

            // Project ocean normal onto camera plane.
            {
                var projectedNormal = new Vector2
                (
                    Vector3.Dot(Vector3.up, camera.transform.right),
                    Vector3.Dot(Vector3.up, camera.transform.up)
                );

                underwaterPostProcessMaterial.SetVector(sp_HorizonNormal, projectedNormal);
            }

            // Compute ambient lighting SH
            {
                // We could pass in a renderer which would prime this lookup. However it doesnt make sense to use an existing render
                // at different position, as this would then thrash it and negate the priming functionality. We could create a dummy invis GO
                // with a dummy Renderer which might be enoguh, but this is hacky enough that we'll wait for it to become a problem
                // rather than add a pre-emptive hack.

                UnityEngine.Profiling.Profiler.BeginSample("Underwater sample spherical harmonics");

                LightProbes.GetInterpolatedProbe(OceanRenderer.Instance.ViewCamera.transform.position, null, out var sphericalHarmonicsL2);
                sphericalHarmonicsL2.Evaluate(sphericalHarmonicsData._shDirections, sphericalHarmonicsData._ambientLighting);
                underwaterPostProcessMaterial.SetVector(sp_AmbientLighting, sphericalHarmonicsData._ambientLighting[0]);

                UnityEngine.Profiling.Profiler.EndSample();
            }
        }
    }
}
