using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace UniCAVE
{
    /// <summary>
    /// URP Renderer Feature that detects a RowInterleaveStereo component on the current camera and injects
    /// a row-interleave blit pass at BeforeRenderingPostProcessing.
    ///
    /// Owns the shader + material: the Inspector-assigned Shader reference is
    /// tracked by Unity's asset dependency graph, so the shader is always bundled
    /// into player builds (Shader.Find at runtime would otherwise be stripped).
    /// </summary>
    public class RowInterleaveStereoFeature : ScriptableRendererFeature
    {
        [SerializeField, Tooltip("Assign RowInterleaveStereo shader")]
        private Shader interleaveShader;

        private Material interleaveMaterial;
        private RowInterleavePass pass;

        private static readonly int LeftTexID  = Shader.PropertyToID("_LeftTex");
        private static readonly int RightTexID = Shader.PropertyToID("_RightTex");
        private static readonly int SwapEyesID = Shader.PropertyToID("_SwapEyes");

        public override void Create()
        {
            pass = new RowInterleavePass();
            // BeforeRenderingPostProcessing runs before post FX and before UI.
            // This guarantees UI (drawn at AfterRendering) appears on top.
            pass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

            // Recreate material on domain reload / feature rebuild.
            if (interleaveMaterial != null)
            {
                CoreUtils.Destroy(interleaveMaterial);
                interleaveMaterial = null;
            }

            if (interleaveShader == null)
            {
                Debug.LogError("[RowInterleaveStereoFeature] Interleave Shader slot is empty. Assign UniCAVE/RowInterleaveStereo on this feature in the URP Renderer asset.");
                return;
            }

            interleaveMaterial = CoreUtils.CreateEngineMaterial(interleaveShader);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(interleaveMaterial);
            interleaveMaterial = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (interleaveMaterial == null) return;

            Camera cam = renderingData.cameraData.camera;
            if (cam == null) return;

            var stereo = cam.GetComponent<RowInterleaveStereo>();
            if (stereo == null || !stereo.IsReady()) return;

            // Push per-frame state from the component into the shared material.
            interleaveMaterial.SetTexture(LeftTexID,  stereo.leftEyeCamera.targetTexture);
            interleaveMaterial.SetTexture(RightTexID, stereo.rightEyeCamera.targetTexture);
            interleaveMaterial.SetFloat(SwapEyesID,   stereo.swapEyes ? 1f : 0f);

            pass.Setup(interleaveMaterial);
            renderer.EnqueuePass(pass);
        }

        // The actual pass: blits interleaved L/R into the active color target.
        // Uses URP 17 Render Graph API.
        private class RowInterleavePass : ScriptableRenderPass
        {
            private Material material;

            public void Setup(Material m)
            {
                material = m;
            }

            private class PassData
            {
                public Material material;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (material == null) return;

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Row Interleave Stereo", out var data))
                {
                    data.material = material;

                    // Write to the active camera color target. URP will later blit this
                    // to the backbuffer and draw UI on top at AfterRendering.
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        // Fullscreen triangle with the interleave material.
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles, 3);
                    });
                }
            }
        }
    }
}
