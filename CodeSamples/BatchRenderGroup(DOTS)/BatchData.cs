using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class BatchData
{
    // Some helper constants to make calculations more convenient.
    protected const int kSizeOfPackedMatrix = sizeof(float) * 4 * 3;
    protected const int kSizeOfFloat4 = sizeof(float) * 4;
    protected const int kExtraBytes = sizeof(float) * 4 * 4 * 2;

    private const int buffersCount = 3;

    private int currentBufferIndex;

    protected BatchID[] batchIDs;
    protected BatchMeshID meshID;
    protected BatchMaterialID materialID;

    // pointer (in bytes) to ObjectToWorld matrix
    public int ByteAddressObjectToWorld;
    // pointer (in bytes) to WorldToObject matrix
    public int ByteAddressWorldToObject;

    private int sortingOrder;

    //link to GPU memory
    protected GraphicsBuffer[] graphicBuffers;
    //how many bytes buffer can contains
    private int windowSize;
    //how many instances we can hold in one window
    protected int instancesPerWindow;
    //if SSBO mode it's 1, if UBO mode - several
    private int windowsCount;
    //have to use constant buffer
    private bool useConstantBuffer;
    //how many bytes we need to draw single entity
    public virtual int BytesPerInstance => (kSizeOfPackedMatrix * 2);
    //old constant kMaxInstances
    private int maxInstances;
    //auxiliary array to determine which entities should be shown and which not
    protected NativeArray<bool> isVisible;

    public BatchData(Material material, Mesh mesh, BatchRendererGroup mBRG, int sortingOrder = 0, int maxInstances = 10000)
    {
        this.maxInstances = maxInstances;
        this.useConstantBuffer = BRGSystem.UseConstantBuffer;
        this.sortingOrder = sortingOrder;
        currentBufferIndex = 0;

        //if we use constant buffer
        if (useConstantBuffer)
        {
            //geting max size of one batch
            windowSize = BatchRendererGroup.GetConstantBufferMaxWindowSize();
            //calculating how many entites we can draw in one batch
            instancesPerWindow = windowSize / BytesPerInstance;
            //calculating how many batches we shoud define to cover maxInstances
            windowsCount = (this.maxInstances + instancesPerWindow - 1) / instancesPerWindow;
        }
        else
        {
            //size of one batch - multiply of BytesPerInstance and maxInstances + kExtraBytes
            windowSize = BRGSystem.BufferCountForInstances(BytesPerInstance, this.maxInstances, kExtraBytes);
            //we can draw all entites in one batch
            instancesPerWindow = this.maxInstances;
            //batch count = 1, because we can draw all entites in one batch
            windowsCount = 1;
        }

        //rgister mesh
        meshID = mBRG.RegisterMesh(mesh);
        //register material
        materialID = mBRG.RegisterMaterial(material);
        //creating metadata to explain shader what data in what position in GPU buffer
        NativeArray<MetadataValue> metaData = GetMetaData();
        //allocate needed amount of batches
        batchIDs = new BatchID[windowsCount * buffersCount];
        //allocate buffers
        graphicBuffers = new GraphicsBuffer[buffersCount];

        for (int i = 0; i < buffersCount; i++)
        {
            //allocate buffer in GPU? memory
            graphicBuffers[i] = new GraphicsBuffer(useConstantBuffer ? GraphicsBuffer.Target.Constant : GraphicsBuffer.Target.Raw,
                GraphicsBuffer.UsageFlags.LockBufferForWrite,
                BRGSystem.BufferCountForInstances(BytesPerInstance, this.maxInstances, kExtraBytes),
                sizeof(int));

            //fill service bytes
            graphicBuffers[i].SetData(new Matrix4x4[1] { Matrix4x4.zero }, 0, 0, 1);

            //register batchId whith buffer
            for (int j = 0; j < windowsCount; j++)
            {
                int offset = j * windowSize;
                batchIDs[i * windowsCount + j] = mBRG.AddBatch(metaData, graphicBuffers[i].bufferHandle, (uint)offset, useConstantBuffer ? (uint)windowSize : 0);
            }
        }
    }

    //helper method to create metadata for GPU buffer and calculate pointers (need them to know in which position in GPU buffer we should write data)
    protected virtual NativeArray<MetadataValue> GetMetaData()
    {
        // pointer (in bytes) to ObjectToWorld matrix
        ByteAddressObjectToWorld = kSizeOfPackedMatrix * 2;
        // pointer (in bytes) to WorldToObject matrix
        ByteAddressWorldToObject = ByteAddressObjectToWorld + kSizeOfPackedMatrix * instancesPerWindow;

        NativeArray<MetadataValue> metadata = new NativeArray<MetadataValue>(2, Allocator.Temp);
        metadata[0] = new MetadataValue
        {
            NameID = Shader.PropertyToID("unity_ObjectToWorld"),
            Value = (uint)(0x80000000 | ByteAddressObjectToWorld)
        };
        metadata[1] = new MetadataValue
        {
            NameID = Shader.PropertyToID("unity_WorldToObject"),
            Value = (uint)(0x80000000 | ByteAddressWorldToObject)
        };

        return metadata;
    }

    public virtual void Dispose()
    {
        for (int i = 0; i < graphicBuffers.Length; i++)
            graphicBuffers[i].Release();

        if (isVisible.IsCreated)
            isVisible.Dispose();
    }

    public int GetCommandsCount(int entitiesCount)
        => useConstantBuffer ? ((entitiesCount + instancesPerWindow - 1) / instancesPerWindow) : 1;

    public virtual NativeArray<float> LockGPUArray(int entitiesCount, out NativeArray<bool> isVisible)
    {
        this.isVisible = isVisible = new NativeArray<bool>(entitiesCount, Allocator.TempJob);
        return LockGPUArray(entitiesCount);
    }

    public virtual NativeArray<float> LockGPUArray(int entitiesCount)
        => graphicBuffers[currentBufferIndex].LockBufferForWrite<float>(0, ArrayForBufferCount(BytesPerInstance * maxInstances, sizeof(float), kExtraBytes));

    public virtual void UnlockGPUArray()
    {
        if (isVisible.IsCreated)
            isVisible.Dispose();

        graphicBuffers[currentBufferIndex].UnlockBufferAfterWrite<float>(ArrayForBufferCount(BytesPerInstance * maxInstances, sizeof(float), kExtraBytes));

        currentBufferIndex = (currentBufferIndex + 1) % buffersCount;
    }

    //create needed amount of draw commands to cover all entities 
    public virtual unsafe void CreateDrawCommands(BatchCullingOutputDrawCommands* drawCommands, ref int drawCommandIndex, int entitiesCount, ref int visibleOffset)
    {
        int commandsCount = GetCommandsCount(entitiesCount);
        for (int i = 0; i < commandsCount; i++)
        {
            int entitiesInCommandCount = SetVisibleIndexes(drawCommands, visibleOffset, i, entitiesCount);
            BRGSystem.FillDrawCommand(drawCommands, drawCommandIndex, (uint)entitiesInCommandCount, batchIDs[currentBufferIndex * windowsCount + i], materialID, meshID, (uint)visibleOffset, sortingOrder);
            visibleOffset += entitiesInCommandCount;
            drawCommandIndex++;
        }
    }

    private int ArrayForBufferCount(int totalBytes, int sizeOfArrayType, int extraBytes = 0)
        => (totalBytes + extraBytes) / sizeOfArrayType;

    //show shader what entities should be shown
    private unsafe int SetVisibleIndexes(BatchCullingOutputDrawCommands* drawCommands, int visibleOffset, int iterationIndex, int entitiesCount)
    {
        int index = 0;
        int instanceIndex = visibleOffset;
        for (int i = iterationIndex * instancesPerWindow; i < entitiesCount && i < (iterationIndex + 1) * instancesPerWindow; i++)
        {
            if ((isVisible.IsCreated && isVisible[i]) || !isVisible.IsCreated)
            {
                drawCommands->visibleInstances[instanceIndex] = index;
                instanceIndex++;
            }
            index++;
        }
        return instanceIndex - visibleOffset;
    }
}
