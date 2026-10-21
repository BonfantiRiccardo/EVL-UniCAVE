using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace UniCAVE
{
    /// <summary>
    /// Composites the left/right eye RenderTextures into row-interleaved output for passive-polarized
    /// 3D displays (CAVE2 Planar panels). Added at runtime by PhysicalDisplay. It injects its own pass
    /// into its camera, so the URP Renderer asset needs no feature.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class RowInterleaveStereo : MonoBehaviour
    {
        [Tooltip("Left eye camera (must render to a RenderTexture)")]
        public Camera leftEyeCamera;

        [Tooltip("Right eye camera (must render to a RenderTexture)")]
        public Camera rightEyeCamera;

        [Tooltip("Swap which eye gets even vs odd rows (depends on panel polarizer orientation)")]
        public bool swapEyes;

        private const string ShaderResourcePath = "RowInterleaveStereo";   // a Resources folder keeps the shader in player builds

        private static readonly int LeftTexID  = Shader.PropertyToID("_LeftTex");
        private static readonly int RightTexID = Shader.PropertyToID("_RightTex");
        private static readonly int SwapEyesID = Shader.PropertyToID("_SwapEyes");

        private Camera cam;
        private Material interleaveMaterial;
        private RowInterleavePass pass;

        public bool IsReady()
        {
            return leftEyeCamera != null && rightEyeCamera != null
                && leftEyeCamera.targetTexture != null && rightEyeCamera.targetTexture != null;
        }

        private void OnEnable()
        {
            cam = GetComponent<Camera>();

            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            if (shader == null)
            {
                Debug.LogError("[RowInterleaveStereo] Shader UniCAVE/RowInterleaveStereo not found in a Resources folder.");
                return;
            }

            interleaveMaterial = CoreUtils.CreateEngineMaterial(shader);
            // Before UI, which URP draws at AfterRendering, so UI stays on top of the composite.
            pass = new RowInterleavePass(interleaveMaterial) { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            CoreUtils.Destroy(interleaveMaterial);
            interleaveMaterial = null;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera != cam || !IsReady()) return;

            interleaveMaterial.SetTexture(LeftTexID,  leftEyeCamera.targetTexture);
            interleaveMaterial.SetTexture(RightTexID, rightEyeCamera.targetTexture);
            interleaveMaterial.SetFloat(SwapEyesID,   swapEyes ? 1f : 0f);

            // URP clears the pass queue only when the camera finishes, so a pass enqueued here runs this frame.
            camera.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(pass);
        }

        // Blits interleaved L/R into the active color target.
        private class RowInterleavePass : ScriptableRenderPass
        {
            private readonly Material material;

            public RowInterleavePass(Material m)
            {
                material = m;
            }

            private class PassData
            {
                public Material material;
                public bool setViewport;   // drawing straight to the window, where several cameras may share it
                public Rect viewport;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Row Interleave Stereo", out var data))
                {
                    data.material = material;
                    data.setViewport = resourceData.isActiveTargetBackBuffer;
                    data.viewport = cameraData.camera.pixelRect;

                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        if (d.setViewport) ctx.cmd.SetViewport(d.viewport);
                        // Fullscreen triangle with the interleave material.
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles, 3);
                    });
                }
            }
        }
    }
}
