// Copyright (c) <2015> <Playdead>
// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE.TXT)
// AUTHOR: Lasse Jon Fuglsang Pedersen <lasse@playdead.com>

using System;
using System.Collections.Generic;
using UnityEngine;

[AddComponentMenu("Playdead/VelocityBufferTag")]
public class VelocityBufferTag : MonoBehaviour
{
    public static List<VelocityBufferTag> activeObjects = new List<VelocityBufferTag>(128);

    [NonSerialized, HideInInspector] internal Mesh mesh;
    [NonSerialized, HideInInspector] internal Matrix4x4 localToWorldPrev;
    [NonSerialized, HideInInspector] internal Matrix4x4 localToWorldCurr;

    private SkinnedMeshRenderer skinnedMesh = null;
	[SerializeField] internal bool useSkinnedMesh = false;

    internal const int framesNotRenderedThreshold = 60;
    private int framesNotRendered = framesNotRenderedThreshold;

    [NonSerialized] internal bool sleeping = false;

    void Start()
    {
		Initialize();
    }

	void Initialize()
	{
		if (useSkinnedMesh)
		{
			var smr = this.GetComponent<SkinnedMeshRenderer>();
			if (smr != null)
			{
				mesh = new Mesh();
				skinnedMesh = smr;
				skinnedMesh.BakeMesh(mesh);
			}
		}
		else
		{
			var mf = this.GetComponent<MeshFilter>();
			if (mf != null)
			{
				mesh = mf.sharedMesh;
			}
		}

		localToWorldCurr = transform.localToWorldMatrix;
		localToWorldPrev = localToWorldCurr;
	}
    void VelocityUpdate()
    {
        if (useSkinnedMesh)
        {
            if (skinnedMesh == null)
            {
                Debug.LogWarning("vbuf skinnedMesh not set, switching to regular mode", this);
				useSkinnedMesh = false; //Fallback to avoid constant spamming of the messag
				Initialize(); //Reinitialize with the normal mesh route
				VelocityUpdate(); //Call this function back again to skip waiting another frame, can't cause loop since useSkinnedMesh set false.
                return;
            }

            if (sleeping)
            {
                skinnedMesh.BakeMesh(mesh);
                mesh.normals = mesh.vertices;// garbage ahoy
            }
            else
            {
                Vector3[] vs = mesh.vertices;// garbage ahoy
                skinnedMesh.BakeMesh(mesh);
                mesh.normals = vs;
            }
        }

        if (sleeping)
        {
            localToWorldCurr = transform.localToWorldMatrix;
            localToWorldPrev = localToWorldCurr;
        }
        else
        {
            localToWorldPrev = localToWorldCurr;
            localToWorldCurr = transform.localToWorldMatrix;
        }

        sleeping = false;
    }

    void LateUpdate()
    {
        if (framesNotRendered < framesNotRenderedThreshold)
        {
            framesNotRendered++;
        }
        else
        {
            sleeping = true;// sleep until next OnWillRenderObject
            return;
        }

        VelocityUpdate();
    }

    void OnWillRenderObject()
    {
        if (Camera.current != Camera.main)
            return;// ignore anything but main cam

        if (sleeping)
        {
            VelocityUpdate();
        }

        framesNotRendered = 0;
    }

    void OnEnable()
    {
        activeObjects.Add(this);
    }

    void OnDisable()
    {
        activeObjects.Remove(this);
    }
}
