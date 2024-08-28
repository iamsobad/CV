using CardTD.Utilities;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public partial class BRGSystem : SystemBase
{
    private static float3 GetScale(float scale, float2 frameScale) => new float3(scale * frameScale.x, scale * frameScale.y, scale);

    private static float3 GetPosition(float2 position, float2 offset)
    {
        //        return new float3(position.x - offset.x, position.y + offset.y, 0);
        //    default:
        //        return new float3(position.x + offset.x, position.y + offset.y, 0);
        return position.ToFloat3();
    }

    public static void FillDataBuffer(ref NativeArray<float> dataBuffer, int addres, int index, float4 vector)
    {
        dataBuffer[addres + index * 4 + 0] = vector.x;
        dataBuffer[addres + index * 4 + 1] = vector.y;
        dataBuffer[addres + index * 4 + 2] = vector.z;
        dataBuffer[addres + index * 4 + 3] = vector.w;
    }
    public static void FillDataBuffer(ref NativeArray<float> dataBuffer, int addres, int index, float4x4 mart)
    {
        dataBuffer[addres + index * 12 + 0] = mart.c0.x;
        dataBuffer[addres + index * 12 + 1] = mart.c0.y;
        dataBuffer[addres + index * 12 + 2] = mart.c0.z;
        dataBuffer[addres + index * 12 + 3] = mart.c1.x;
        dataBuffer[addres + index * 12 + 4] = mart.c1.y;
        dataBuffer[addres + index * 12 + 5] = mart.c1.z;
        dataBuffer[addres + index * 12 + 6] = mart.c2.x;
        dataBuffer[addres + index * 12 + 7] = mart.c2.y;
        dataBuffer[addres + index * 12 + 8] = mart.c2.z;
        dataBuffer[addres + index * 12 + 9] = mart.c3.x;
        dataBuffer[addres + index * 12 + 10] = mart.c3.y;
        dataBuffer[addres + index * 12 + 11] = mart.c3.z;
    }

    public static unsafe void SetVisibleIndexes(BatchCullingOutputDrawCommands* drawCommands, ref int visibleOffset, int entitiesCount)
    {
        int index = 0;
        int length = visibleOffset + entitiesCount;
        for (int i = visibleOffset; i < length; ++i)
        {
            drawCommands->visibleInstances[i] = index;
            index++;
        }
        visibleOffset += entitiesCount;
    }

    //public static unsafe int SetVisibleIndexes(BatchCullingOutputDrawCommands* drawCommands, int visibleOffset, NativeArray<bool> isVisible)
    //{
    //    int index = visibleOffset;
    //    for (int i = 0; i < isVisible.Length; i++)
    //    {
    //        if (isVisible[i])
    //        {
    //            drawCommands->visibleInstances[index] = i;
    //            index++;
    //        }
    //    }
    //    return index - visibleOffset;
    //}


    public static unsafe void FillDrawCommand(BatchCullingOutputDrawCommands* drawCommands, int i, uint entityCount, BatchID batchID, BatchMaterialID materialID, BatchMeshID meshID, uint visibleOffset, int sortPosition = 0)
    {
        // Configure the single draw command to draw kNumInstances instances
        // starting from offset 0 in the array, using the batch, material and mesh
        // IDs registered in the Start() method. It doesn't set any special flags.
        drawCommands->drawCommands[i].visibleOffset = visibleOffset;
        drawCommands->drawCommands[i].visibleCount = entityCount;
        drawCommands->drawCommands[i].batchID = batchID;
        drawCommands->drawCommands[i].materialID = materialID;
        drawCommands->drawCommands[i].meshID = meshID;
        drawCommands->drawCommands[i].submeshIndex = 0;
        drawCommands->drawCommands[i].splitVisibilityMask = 0xff;
        drawCommands->drawCommands[i].flags = 0;
        drawCommands->drawCommands[i].sortingPosition = i + sortPosition;
    }

    public static unsafe BatchCullingOutputDrawCommands* AllocateMemory(BatchCullingOutput cullingOutput, int visibleInstancesCount, int batchesCount)
    {
        // UnsafeUtility.Malloc() requires an alignment, so use the largest integer type's alignment
        // which is a reasonable default.
        int alignment = UnsafeUtility.AlignOf<long>();

        // Acquire a pointer to the BatchCullingOutputDrawCommands struct so you can easily
        // modify it directly.
        BatchCullingOutputDrawCommands* drawCommands = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();

        // Allocate memory for the output arrays. In a more complicated implementation, you would calculate
        // the amount of memory to allocate dynamically based on what is visible.
        // This example assumes that all of the instances are visible and thus allocates
        // memory for each of them. The necessary allocations are as follows:
        // - a single draw command (which draws kNumInstances instances)
        // - a single draw range (which covers our single draw command)
        // - kNumInstances visible instance indices.
        // You must always allocate the arrays using Allocator.TempJob.
        drawCommands->drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawCommand>() * batchesCount, alignment, Allocator.TempJob);
        drawCommands->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawRange>(), alignment, Allocator.TempJob);
        drawCommands->visibleInstances = (int*)UnsafeUtility.Malloc(visibleInstancesCount * sizeof(int), alignment, Allocator.TempJob);
        drawCommands->drawCommandPickingInstanceIDs = null;

        drawCommands->drawCommandCount = batchesCount;
        drawCommands->drawRangeCount = 1; // batchesCount
        drawCommands->visibleInstanceCount = visibleInstancesCount;

        // This example doens't use depth sorting, so it leaves instanceSortingPositions as null.
        drawCommands->instanceSortingPositions = null;
        drawCommands->instanceSortingPositionFloatCount = 0;
        return drawCommands;
    }

    // Raw buffers are allocated in ints. This is a utility method that calculates
    // the required number of ints for the data.
    public static int BufferCountForInstances(int bytesPerInstance, int numInstances, int extraBytes = 0)
    {
        // Round byte counts to int multiples
        int intSize = sizeof(int);
        bytesPerInstance = (bytesPerInstance + intSize - 1) / intSize * intSize;
        extraBytes = (extraBytes + intSize - 1) / intSize * intSize;
        int totalBytes = bytesPerInstance * numInstances + extraBytes;
        if (totalBytes < intSize)
            return 0;
        return totalBytes / intSize;
    }
}
