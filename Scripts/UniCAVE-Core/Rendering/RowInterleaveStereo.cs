using UnityEngine;

namespace UniCAVE
{
    /// <summary>
    /// Data-only component describing how left/right eye RenderTextures should be
    /// composited into a row-interleaved output for passive-polarized 3D displays
    /// (CAVE2 Planar panels). Added at runtime by PhysicalDisplay.
    ///
    /// The actual shader + material are owned by RowInterleaveStereoFeature (a
    /// ScriptableRendererFeature on the URP Renderer asset), which guarantees the
    /// shader is bundled into player builds via the Unity asset dependency graph.
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

        public bool IsReady()
        {
            return leftEyeCamera != null && rightEyeCamera != null
                && leftEyeCamera.targetTexture != null && rightEyeCamera.targetTexture != null;
        }
    }
}
