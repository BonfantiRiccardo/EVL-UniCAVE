using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UniCAVE
{
    /// <summary>
    /// Copy non-stereo camera output to stereo camera output
    /// Essentially allows two cameras to render to Unity's passive stereo pipeline
    /// URP-compatible version using RenderPipelineManager callbacks
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class StereoBlit : MonoBehaviour
    {
        public Camera lcam;
        public Camera rcam;

        private Camera cam;

        /// <summary>
        /// Assign my camera and create target camera render textures
        /// </summary>
        private void Start()
        {
            cam = GetComponent<Camera>();
            cam.depth = 100;

            if(lcam.targetTexture == null)
            {
                lcam.targetTexture = new RenderTexture(lcam.pixelWidth, lcam.pixelHeight, 24);
                lcam.targetTexture.name = Util.ObjectFullName(gameObject) + "_LeftTexture";
            }
            if(rcam.targetTexture == null)
            {
                rcam.targetTexture = new RenderTexture(rcam.pixelWidth, rcam.pixelHeight, 24);
                rcam.targetTexture.name = Util.ObjectFullName(gameObject) + "_RightTexture";
            }
        }

        private void OnEnable()
        {
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        }

        /// <summary>
        /// URP-compatible replacement for OnRenderImage.
        /// Blits left/right camera textures to the center camera output.
        /// </summary>
        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if(camera != cam) return;
            if(lcam == null || rcam == null) return;
            if(lcam.targetTexture == null || rcam.targetTexture == null) return;

            CommandBuffer cmd = CommandBufferPool.Get("StereoBlit");
            // Blit left camera texture as the center camera's output
            cmd.Blit(lcam.targetTexture, BuiltinRenderTextureType.CameraTarget);
            context.ExecuteCommandBuffer(cmd);
            context.Submit();
            CommandBufferPool.Release(cmd);
        }
    }
}