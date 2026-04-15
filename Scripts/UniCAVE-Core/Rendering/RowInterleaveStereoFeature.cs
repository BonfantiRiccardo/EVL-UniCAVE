using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace UniCAVE
{
    /// <summary>
    /// URP Renderer Feature that detects a RowInterleaveStereo component on the
    /// current camera and injects a row-interleave blit pass at
    /// BeforeRenderingPostProcessing -- BEFORE URP's DrawScreenSpaceUI pass,
    /// so UI is drawn on top of the interleaved stereo image instead of being
    /// overwritten by it.
    ///
    /// Add this feature to your URP Renderer asset once; it becomes active
    /// only on cameras that have a RowInterleaveStereo component.
    /// </summary>
    public class RowInterleaveStereoFeature : ScriptableRendererFeature
    {
        private RowInterleavePass pass;

        public override void Create()
        {
            pass = new RowInterleavePass();
            // BeforeRenderingPostProcessing runs before post FX and before UI.
            // This guarantees UI (drawn at AfterRendering) appears on top.
            pass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            if (cam == null) return;

            var stereo = cam.GetComponent<RowInterleaveStereo>();
            if (stereo == null || !stereo.IsReady()) return;

            pass.Setup(stereo);
            renderer.EnqueuePass(pass);
        }

        // -------------------------------------------------------------------
        // The actual pass: blits interleaved L/R into the active color target.
        // Uses URP 17 Render Graph API.
        // -------------------------------------------------------------------
        private class RowInterleavePass : ScriptableRenderPass
        {
            private RowInterleaveStereo stereo;

            public void Setup(RowInterleaveStereo s)
            {
                stereo = s;
            }

            private class PassData
            {
                public Material material;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (stereo == null || !stereo.IsReady()) return;

                // Update material texture/float properties; the material holds references
                // to the eye RenderTextures directly so we don't need to import them into
                // the render graph as TextureHandles.
                stereo.UpdateMaterialParams();

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Row Interleave Stereo", out var data))
                {
                    data.material = stereo.InterleaveMaterial;

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
