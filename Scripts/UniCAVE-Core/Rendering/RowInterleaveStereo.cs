using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace UniCAVE
{
    /// <summary>
    /// Composites left and right eye RenderTextures into a row-interleaved
    /// output for passive-polarized 3D displays (e.g. CAVE2 Planar panels).
    ///
    /// Attach to the compositing camera (cullingMask = Nothing, high depth).
    /// The ScriptableRendererFeature below injects the interleave blit at
    /// BeforeRenderingPostProcessing so URP's screen-space UI (and any other
    /// post-UI passes) draws ON TOP of the interleaved result.
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

        private Material interleaveMaterial;
        private Camera compositingCamera;

        private static readonly int LeftTexID  = Shader.PropertyToID("_LeftTex");
        private static readonly int RightTexID = Shader.PropertyToID("_RightTex");
        private static readonly int SwapEyesID = Shader.PropertyToID("_SwapEyes");

        public Material InterleaveMaterial => interleaveMaterial;
        public Camera   CompositingCamera  => compositingCamera;

        public bool IsReady()
        {
            return interleaveMaterial != null
                && leftEyeCamera != null && rightEyeCamera != null
                && leftEyeCamera.targetTexture != null && rightEyeCamera.targetTexture != null;
        }

        void Awake()
        {
            compositingCamera = GetComponent<Camera>();

            Shader shader = Shader.Find("UniCAVE/RowInterleaveStereo");
            if (shader == null)
            {
                Debug.LogError("[RowInterleaveStereo] Shader 'UniCAVE/RowInterleaveStereo' not found.");
                enabled = false;
                return;
            }

            interleaveMaterial = new Material(shader);
            interleaveMaterial.hideFlags = HideFlags.HideAndDontSave;
        }

        void OnDestroy()
        {
            if (interleaveMaterial != null)
            {
                DestroyImmediate(interleaveMaterial);
                interleaveMaterial = null;
            }
        }

        /// <summary>
        /// Update shader parameters. Called by the injected pass just before blitting.
        /// </summary>
        public void UpdateMaterialParams()
        {
            if (interleaveMaterial == null) return;
            if (leftEyeCamera != null && leftEyeCamera.targetTexture != null)
                interleaveMaterial.SetTexture(LeftTexID, leftEyeCamera.targetTexture);
            if (rightEyeCamera != null && rightEyeCamera.targetTexture != null)
                interleaveMaterial.SetTexture(RightTexID, rightEyeCamera.targetTexture);
            interleaveMaterial.SetFloat(SwapEyesID, swapEyes ? 1f : 0f);
        }
    }
}
