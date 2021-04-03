// Copyright (c) <2015> <Playdead>
// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE.TXT)
// AUTHOR: Lasse Jon Fuglsang Pedersen <lasse@playdead.com>

#if UNITY_5_5_OR_NEWER
#define SUPPORT_STEREO
#endif

using UnityEngine;

[ExecuteInEditMode]
[RequireComponent(typeof(Camera), typeof(FrustumJitter), typeof(VelocityBuffer))]
[AddComponentMenu("Playdead/TemporalReprojection")]
public class TemporalReprojection : EffectBase
{
    private static RenderBuffer[] mrt = new RenderBuffer[2];

    private Camera _camera;
    private FrustumJitter _frustumJitter;
    private VelocityBuffer _velocityBuffer;

    public Shader reprojectionShader;
    private Material reprojectionMaterial;
    private RenderTexture[,] reprojectionBuffer;
    private int[] reprojectionIndex = new int[2] { -1, -1 };

    public enum Neighborhood
    {
        MinMax3x3,
        MinMax3x3Rounded,
        MinMax4TapVarying,
    };

    public Neighborhood neighborhood = Neighborhood.MinMax3x3Rounded;
    public bool unjitterColorSamples = true;
    public bool unjitterNeighborhood = false;
    public bool unjitterReprojection = false;
    public bool useYCoCg = false;
    public bool useClipping = true;
    public bool useDilation = true;
    public bool useMotionBlur = true;
    public bool useOptimizations = true;

    [Range(0.0f, 1.0f)] public float feedbackMin = 0.88f;
    [Range(0.0f, 1.0f)] public float feedbackMax = 0.97f;

    public float motionBlurStrength = 1.0f;
    public bool motionBlurIgnoreFF = false;

    void Reset()
    {
        _camera = GetComponent<Camera>();
        _frustumJitter = GetComponent<FrustumJitter>();
        _velocityBuffer = GetComponent<VelocityBuffer>();
    }

    void Clear()
    {
        EnsureArray(ref reprojectionIndex, 2);
        reprojectionIndex[0] = -1;
        reprojectionIndex[1] = -1;
    }

    void Awake()
    {
        Reset();
        Clear();
    }

    void Resolve(RenderTexture source, RenderTexture destination)
    {
        EnsureArray(ref reprojectionBuffer, 2, 2);
        EnsureArray(ref reprojectionIndex, 2, initialValue: -1);

        EnsureMaterial(ref reprojectionMaterial, reprojectionShader);
        if (reprojectionMaterial == null)
        {
            Graphics.Blit(source, destination);
            return;
        }

#if SUPPORT_STEREO
        int eyeIndex = (_camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right) ? 1 : 0;
#else
        int eyeIndex = 0;
#endif
        int bufferW = source.width;
        int bufferH = source.height;

        if (EnsureRenderTarget(ref reprojectionBuffer[eyeIndex, 0], bufferW, bufferH, RenderTextureFormat.ARGB32, FilterMode.Bilinear, antiAliasing: source.antiAliasing))
            Clear();
        if (EnsureRenderTarget(ref reprojectionBuffer[eyeIndex, 1], bufferW, bufferH, RenderTextureFormat.ARGB32, FilterMode.Bilinear, antiAliasing: source.antiAliasing))
            Clear();

#if SUPPORT_STEREO
        bool stereoEnabled = _camera.stereoEnabled;
#else
        bool stereoEnabled = false;
#endif
#if UNITY_EDITOR
        bool allowMotionBlur = !stereoEnabled && Application.isPlaying;
#else
        bool allowMotionBlur = !stereoEnabled;
#endif

        EnsureKeyword(reprojectionMaterial, "CAMERA_PERSPECTIVE", !_camera.orthographic);
        EnsureKeyword(reprojectionMaterial, "CAMERA_ORTHOGRAPHIC", _camera.orthographic);

        EnsureKeyword(reprojectionMaterial, "MINMAX_3X3", neighborhood == Neighborhood.MinMax3x3);
        EnsureKeyword(reprojectionMaterial, "MINMAX_3X3_ROUNDED", neighborhood == Neighborhood.MinMax3x3Rounded);
        EnsureKeyword(reprojectionMaterial, "MINMAX_4TAP_VARYING", neighborhood == Neighborhood.MinMax4TapVarying);
        EnsureKeyword(reprojectionMaterial, "UNJITTER_COLORSAMPLES", unjitterColorSamples);
        EnsureKeyword(reprojectionMaterial, "UNJITTER_NEIGHBORHOOD", unjitterNeighborhood);
        EnsureKeyword(reprojectionMaterial, "UNJITTER_REPROJECTION", unjitterReprojection);
        EnsureKeyword(reprojectionMaterial, "USE_YCOCG", useYCoCg);
        EnsureKeyword(reprojectionMaterial, "USE_CLIPPING", useClipping);
        EnsureKeyword(reprojectionMaterial, "USE_DILATION", useDilation);
        EnsureKeyword(reprojectionMaterial, "USE_MOTION_BLUR", useMotionBlur && allowMotionBlur);
        EnsureKeyword(reprojectionMaterial, "USE_MOTION_BLUR_NEIGHBORMAX", _velocityBuffer.activeVelocityNeighborMax != null);
        EnsureKeyword(reprojectionMaterial, "USE_OPTIMIZATIONS", useOptimizations);

        if (reprojectionIndex[eyeIndex] == -1)// bootstrap
        {
            reprojectionIndex[eyeIndex] = 0;
            reprojectionBuffer[eyeIndex, reprojectionIndex[eyeIndex]].DiscardContents();
            Graphics.Blit(source, reprojectionBuffer[eyeIndex, reprojectionIndex[eyeIndex]]);
        }

        int indexRead = reprojectionIndex[eyeIndex];
        int indexWrite = (reprojectionIndex[eyeIndex] + 1) % 2;

        Vector4 jitterUV = _frustumJitter.activeSample;
        jitterUV.x /= source.width;
        jitterUV.y /= source.height;
        jitterUV.z /= source.width;
        jitterUV.w /= source.height;

        reprojectionMaterial.SetVector("_JitterUV", jitterUV);
        reprojectionMaterial.SetTexture("_VelocityBuffer", _velocityBuffer.activeVelocityBuffer);
        reprojectionMaterial.SetTexture("_VelocityNeighborMax", _velocityBuffer.activeVelocityNeighborMax);
        reprojectionMaterial.SetTexture("_MainTex", source);
        reprojectionMaterial.SetTexture("_PrevTex", reprojectionBuffer[eyeIndex, indexRead]);
        reprojectionMaterial.SetFloat("_FeedbackMin", feedbackMin);
        reprojectionMaterial.SetFloat("_FeedbackMax", feedbackMax);
        reprojectionMaterial.SetFloat("_MotionScale", motionBlurStrength * (motionBlurIgnoreFF ? Mathf.Min(1.0f, 1.0f / _velocityBuffer.timeScale) : 1.0f));

        // reproject frame n-1 into output + history buffer
        {
            mrt[0] = reprojectionBuffer[eyeIndex, indexWrite].colorBuffer;
            mrt[1] = destination.colorBuffer;

            reprojectionBuffer[eyeIndex, indexWrite].DiscardContents();
            Graphics.SetRenderTarget(mrt, source.depthBuffer);
            reprojectionMaterial.SetPass(0);

            DrawFullscreenQuad();

            reprojectionIndex[eyeIndex] = indexWrite;
        }
    }

    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (destination != null && source.antiAliasing == destination.antiAliasing)// resolve without additional blit when not end of chain
        {
            Resolve(source, destination);
        }
        else
        {
            RenderTexture internalDestination = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, source.antiAliasing);
            {
                Resolve(source, internalDestination);
                Graphics.Blit(internalDestination, destination);
            }
            RenderTexture.ReleaseTemporary(internalDestination);
        }
    }

    void OnApplicationQuit()
    {
        if (reprojectionBuffer != null)
        {
            ReleaseRenderTarget(ref reprojectionBuffer[0, 0]);
            ReleaseRenderTarget(ref reprojectionBuffer[0, 1]);
            ReleaseRenderTarget(ref reprojectionBuffer[1, 0]);
            ReleaseRenderTarget(ref reprojectionBuffer[1, 1]);
        }
    }
}