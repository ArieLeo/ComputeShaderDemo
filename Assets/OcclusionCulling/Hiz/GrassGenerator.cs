using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class GrassGenerator : MonoBehaviour
{
    public Mesh grassMesh;
    public int subMeshIndex = 0;
    public Material grassMaterial;
    public int GrassCountPerRaw = 300;//ÿ�вݵ�����
    public DepthTextureGenerator depthTextureGenerator;
    public ComputeShader compute;//�޳���ComputeShader

    int m_grassCount;
    int kernel;
    Camera mainCamera;

    ComputeBuffer argsBuffer;
    ComputeBuffer grassMatrixBuffer;//���вݵ������������
    ComputeBuffer cullResultBuffer;//�޳���Ľ��
    ComputeBuffer cullResultCount;//�޳��������

    uint[] args = new uint[5] { 0, 0, 0, 0, 0 };
    uint[] cullResultCountArray = new uint[1] { 0 };

    int cullResultBufferId, vpMatrixId, positionBufferId, hizTextureId, shadowMapTextureId;
    int shadowCasterPassIndex;

    void Start()
    {
        m_grassCount = GrassCountPerRaw * GrassCountPerRaw;
        mainCamera = Camera.main;

        if(grassMesh != null) {
            args[0] = grassMesh.GetIndexCount(subMeshIndex);
            args[2] = grassMesh.GetIndexStart(subMeshIndex);
            args[3] = grassMesh.GetBaseVertex(subMeshIndex);
        }
        else
            args[0] = args[1] = args[2] = args[3] = 0;

        InitComputeBuffer();
        InitGrassPosition();
        InitComputeShader();
        AddCommandBuffer();
    }

    void InitComputeShader() {
        kernel = compute.FindKernel("GrassCulling");
        compute.SetInt("grassCount", m_grassCount);
        compute.SetInt("depthTextureSize", depthTextureGenerator.depthTextureSize);
        Debug.Log(Camera.main.projectionMatrix);
        Debug.Log(GL.GetGPUProjectionMatrix(Camera.main.projectionMatrix, false));
        compute.SetBool("isOpenGL", Camera.main.projectionMatrix.Equals(GL.GetGPUProjectionMatrix(Camera.main.projectionMatrix, false)));
        compute.SetBuffer(kernel, "grassMatrixBuffer", grassMatrixBuffer);
        
        cullResultBufferId = Shader.PropertyToID("cullResultBuffer");
        vpMatrixId = Shader.PropertyToID("vpMatrix");
        hizTextureId = Shader.PropertyToID("hizTexture");
        positionBufferId = Shader.PropertyToID("positionBuffer");
        shadowMapTextureId = Shader.PropertyToID("_ShadowMapTexture");
    }

    void InitComputeBuffer() {
        if(grassMatrixBuffer != null) return;
        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        grassMatrixBuffer = new ComputeBuffer(m_grassCount, sizeof(float) * 16);
        cullResultBuffer = new ComputeBuffer(m_grassCount, sizeof(float) * 16, ComputeBufferType.Append);
        cullResultCount = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.IndirectArguments);
    }

    void AddCommandBuffer()
    {
        //���Ʋ�
        CommandBuffer afterForwardOpaqueBuffer = new CommandBuffer();
        afterForwardOpaqueBuffer.EnableShaderKeyword("LIGHTPROBE_SH");
        afterForwardOpaqueBuffer.EnableShaderKeyword("SHADOWS_SCREEN");
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        block.SetTexture(shadowMapTextureId, Shader.GetGlobalTexture(shadowMapTextureId));
        afterForwardOpaqueBuffer.DrawMeshInstancedIndirect(grassMesh, subMeshIndex, grassMaterial, 0, argsBuffer, 0, block);
        mainCamera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, afterForwardOpaqueBuffer);

        //�������ͼ
        shadowCasterPassIndex = grassMaterial.FindPass("ShadowCaster");
        //CommandBuffer afterDepthTextureBuffer = new CommandBuffer();
        //afterDepthTextureBuffer.DrawMeshInstancedIndirect(grassMesh, subMeshIndex, grassMaterial, shadowCasterPassIndex, argsBuffer);
        //mainCamera.AddCommandBuffer(CameraEvent.AfterDepthTexture, afterDepthTextureBuffer);

        //������Ӱ
        CommandBuffer afterShadowMapPassBuffer = new CommandBuffer();
        afterShadowMapPassBuffer.DrawMeshInstancedIndirect(grassMesh, subMeshIndex, grassMaterial, shadowCasterPassIndex, argsBuffer);
        Light light = GameObject.Find("Directional Light").GetComponent<Light>();
        light.AddCommandBuffer(LightEvent.AfterShadowMapPass, afterShadowMapPassBuffer);
    }

    //void Update()
    //{
    //    //��ִ���ⲽ�Ļ���shadowCasterPassIndex �� afterShadowMapPassBuffer ʱ���ݵ�����Ϊ0
    //    args[1] = (uint)m_grassCount;
    //    argsBuffer.SetData(args);
    //}

    void OnPostRender()
    {
        compute.SetMatrix(vpMatrixId, GL.GetGPUProjectionMatrix(mainCamera.projectionMatrix, false) * mainCamera.worldToCameraMatrix);
        cullResultBuffer.SetCounterValue(0);
        compute.SetBuffer(kernel, cullResultBufferId, cullResultBuffer);
        compute.SetTexture(kernel, hizTextureId, depthTextureGenerator.depthTexture);
        compute.Dispatch(kernel, 1 + m_grassCount / 640, 1, 1);
        grassMaterial.SetBuffer(positionBufferId, cullResultBuffer);

        //��ȡʵ��Ҫ��Ⱦ������
        ComputeBuffer.CopyCount(cullResultBuffer, cullResultCount, 0);
        cullResultCount.GetData(cullResultCountArray);
        args[1] = cullResultCountArray[0];
        argsBuffer.SetData(args);

        //Graphics.DrawMeshInstancedIndirect(grassMesh, subMeshIndex, grassMaterial, new Bounds(Vector3.zero, new Vector3(100.0f, 100.0f, 100.0f)), argsBuffer);
    }

    //��ȡÿ���ݵ������������
    void InitGrassPosition() {
        const int padding = 2;
        int width = (100 - padding * 2);
        int widthStart = -width / 2;
        float step = (float)width / GrassCountPerRaw;
        Matrix4x4[] grassMatrixs = new Matrix4x4[m_grassCount];
        for(int i = 0; i < GrassCountPerRaw; i++) {
            for(int j = 0; j < GrassCountPerRaw; j++) {
                Vector2 xz = new Vector2(widthStart + step * i, widthStart + step * j);
                Vector3 position = new Vector3(xz.x, GetGroundHeight(xz), xz.y);
                grassMatrixs[i * GrassCountPerRaw + j] = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
            }
        }
        grassMatrixBuffer.SetData(grassMatrixs);
    }

    //ͨ��Raycast����ݵĸ߶�
    float GetGroundHeight(Vector2 xz) {
        RaycastHit hit;
        if(Physics.Raycast(new Vector3(xz.x, 10, xz.y), Vector3.down, out hit, 20)) {
            return 10 - hit.distance;
        }
        return 0;
    }

    void OnDisable() {
        grassMatrixBuffer?.Release();
        grassMatrixBuffer = null;

        cullResultBuffer?.Release();
        cullResultBuffer = null;

        cullResultCount?.Release();
        cullResultCount = null;

        argsBuffer?.Release();
        argsBuffer = null;
    }
}
