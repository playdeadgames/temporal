// Copyright (c) <2015> <Playdead>
// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE.TXT)
// AUTHOR: Lasse Jon Fuglsang Pedersen <lasse@playdead.com>

using UnityEngine;

[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(FrustumJitter))]
[RequireComponent(typeof(VelocityBuffer))]
[AddComponentMenu("Playdead/TemporalReprojection")]
public class TemporalReprojection : EffectBase
{
    private static RenderBuffer[] mrt = new RenderBuffer[2];

    public Shader reprojectionShader;
    private Material reprojectionMaterial;
    private Matrix4x4[] reprojectionMatrix;
    private RenderTexture[] reprojectionBuffer;
    private int reprojectionIndex = 0;

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

    [Range(0f, 1f)] public float feedbackMin = 0.88f;
    [Range(0f, 1f)] public float feedbackMax = 0.97f;

    public float motionBlurStrength = 1f;
    public bool motionBlurIgnoreFF = false;

    private Camera _camera;
    private FrustumJitter _jitter;
    private VelocityBuffer _velocity;

    void Awake()
    {
        _camera = GetComponent<Camera>();
        _jitter = GetComponent<FrustumJitter>();
        _velocity = GetComponent<VelocityBuffer>();
    }
	//Make it simpler to Enable/Disable the whole effect
	void OnDisable()
	{
		_jitter.enabled = false;
		_velocity.enabled = false;
	}
	void OnEnable()
	{
		_jitter.enabled = true;
		_velocity.enabled = true;
	}

    void Resolve(RenderTexture source, RenderTexture destination)
    {
        EnsureMaterial(ref reprojectionMaterial, reprojectionShader);

        if ( _camera.depthTextureMode == DepthTextureMode.None || reprojectionMaterial == null)
        {
            Graphics.Blit(source, destination);
            if (_camera.depthTextureMode == DepthTextureMode.None)
                _camera.depthTextureMode = DepthTextureMode.Depth;
            return;
        }

        if (reprojectionMatrix == null || reprojectionMatrix.Length != 2)
            reprojectionMatrix = new Matrix4x4[2];
        if (reprojectionBuffer == null || reprojectionBuffer.Length != 2)
            reprojectionBuffer = new RenderTexture[2];

        int bufferW = source.width;
        int bufferH = source.height;

        EnsureRenderTarget(ref reprojectionBuffer[0], bufferW, bufferH, RenderTextureFormat.ARGB32, FilterMode.Bilinear);
        EnsureRenderTarget(ref reprojectionBuffer[1], bufferW, bufferH, RenderTextureFormat.ARGB32, FilterMode.Bilinear);

        EnsureKeyword(reprojectionMaterial, "MINMAX_3X3", neighborhood == Neighborhood.MinMax3x3);
        EnsureKeyword(reprojectionMaterial, "MINMAX_3X3_ROUNDED", neighborhood == Neighborhood.MinMax3x3Rounded);
        EnsureKeyword(reprojectionMaterial, "MINMAX_4TAP_VARYING", neighborhood == Neighborhood.MinMax4TapVarying);
        EnsureKeyword(reprojectionMaterial, "UNJITTER_COLORSAMPLES", unjitterColorSamples);
        EnsureKeyword(reprojectionMaterial, "UNJITTER_NEIGHBORHOOD", unjitterNeighborhood);
        EnsureKeyword(reprojectionMaterial, "UNJITTER_REPROJECTION", unjitterReprojection);
        EnsureKeyword(reprojectionMaterial, "USE_YCOCG", useYCoCg);
        EnsureKeyword(reprojectionMaterial, "USE_CLIPPING", useClipping);
        EnsureKeyword(reprojectionMaterial, "USE_DILATION", useDilation);
#if UNITY_EDITOR
        EnsureKeyword(reprojectionMaterial, "USE_MOTION_BLUR", Application.isPlaying ? useMotionBlur : false);
#else
        EnsureKeyword(reprojectionMaterial, "USE_MOTION_BLUR", useMotionBlur);
#endif
        EnsureKeyword(reprojectionMaterial, "USE_MOTION_BLUR_NEIGHBORMAX", _velocity.velocityNeighborMax != null);
        EnsureKeyword(reprojectionMaterial, "USE_OPTIMIZATIONS", useOptimizations);

        Matrix4x4 cameraP = _camera.GetPerspectiveProjection();
        Matrix4x4 cameraVP = cameraP * _camera.worldToCameraMatrix;

        float oneExtentY = Mathf.Tan(0.5f * Mathf.Deg2Rad * _camera.fieldOfView);
        float oneExtentX = oneExtentY * _camera.aspect;

        if (reprojectionIndex == -1)// bootstrap
        {
            reprojectionIndex = 0;
            reprojectionMatrix[reprojectionIndex] = cameraVP;
            Graphics.Blit(source, reprojectionBuffer[reprojectionIndex]);
        }

        int indexRead = reprojectionIndex;
        int indexWrite = (reprojectionIndex + 1) % 2;

        reprojectionMaterial.SetTexture("_VelocityBuffer", _velocity.velocityBuffer);
        reprojectionMaterial.SetTexture("_VelocityNeighborMax", _velocity.velocityNeighborMax);
        reprojectionMaterial.SetVector("_Corner", new Vector4(oneExtentX, oneExtentY, 0f, 0f));
        reprojectionMaterial.SetVector("_Jitter", _jitter.activeSample);
        reprojectionMaterial.SetMatrix("_PrevVP", reprojectionMatrix[indexRead]);
        reprojectionMaterial.SetTexture("_MainTex", source);
        reprojectionMaterial.SetTexture("_PrevTex", reprojectionBuffer[indexRead]);
        reprojectionMaterial.SetFloat("_FeedbackMin", feedbackMin);
        reprojectionMaterial.SetFloat("_FeedbackMax", feedbackMax);
        reprojectionMaterial.SetFloat("_MotionScale", motionBlurStrength * (motionBlurIgnoreFF ? Mathf.Min(1f, 1f / _velocity.timeScale) : 1f));

        // reproject frame n-1 into output + history buffer
        {
            mrt[0] = reprojectionBuffer[indexWrite].colorBuffer;
            mrt[1] = destination.colorBuffer;

            Graphics.SetRenderTarget(mrt, source.depthBuffer);
            reprojectionMaterial.SetPass(0);

            FullScreenQuad();

            reprojectionMatrix[indexWrite] = cameraVP;
            reprojectionIndex = indexWrite;
        }
    }
    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (destination != null)// resolve without additional blit when not end of chain
        {
            Resolve(source, destination);
        }
        else
        {
            RenderTexture internalDestination = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            {
                Resolve(source, internalDestination);
                Graphics.Blit(internalDestination, destination);
            }
            RenderTexture.ReleaseTemporary(internalDestination);
        }
    }
}
