using CardTD.Utilities;
using ECSTest.Components;
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

//[DisableAutoCreation]
[BurstCompile]
public partial class BRGSystem : SystemBase
{
    /// <summary>
    /// List of data for render one batch (one unit type - one batch with unic texture and mesh)
    /// </summary>
    private List<CreepBatchData> batchDatas = new List<CreepBatchData>();

    private HpBarBatchData hpBarBatchData;
    private BatchData warningBatchData;
    private BatchData stunBatchData;
    private BatchData fearBatchData;

    private ShootVfxBatchData muzzleBatchData;
    private ShootVfxBatchData impactBatchData;
    private NativeArray<int> muzzleIndexes;
    private NativeArray<int> impactIndexes;
    private NativeArray<ShootVfxAnimationFrameData> muzzleFrames;//TODO: transfer to ShootVfxBatchData
    private NativeArray<ShootVfxAnimationFrameData> impactFrames;//TODO: transfer to ShootVfxBatchData
    private int maxTowerIdCount;

    private Dictionary<AllEnums.CreepType, SharedRenderData> sharedRenderDatas;

    private BatchRendererGroup mBRG;

    private EntityQuery creepsQuery;
    private EntityQuery hpQuery;
    private EntityQuery newCreepsQuery;
    private EntityQuery muzzleTimedEventQuery;
    private EntityQuery impactTimedEventQuery;

    private JobHandle combinedHandle;

    public static bool UseConstantBuffer => BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;

    protected override void OnCreate()
    {
        base.OnCreate();

        creepsQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<PositionComponent, AnimationComponent, SharedCreepData, SharedRenderData, Movable, StunComponent, FearComponent>()
            .WithNone<SpawnComponent>()
            .Build(this);

        hpQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<PositionComponent, CreepComponent, SharedRenderData>()
            .Build(this);

        newCreepsQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<PositionComponent, SharedCreepData, Movable, StunComponent, FearComponent, CreepComponent>()
            .WithNone<AnimationComponent>()
            .Build(this);

        muzzleTimedEventQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithDisabled<MuzzleTimedEvent>()
            .Build(this);

        impactTimedEventQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<ImpactTimedEvent>()
            .Build(this);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        Clear();
    }

    public void Clear()
    {
        foreach (CreepBatchData data in batchDatas)
            data.Dispose();
        batchDatas.Clear();

        hpBarBatchData?.Dispose();
        hpBarBatchData = null;

        warningBatchData?.Dispose();
        warningBatchData = null;

        stunBatchData?.Dispose();
        stunBatchData = null;

        fearBatchData?.Dispose();
        fearBatchData = null;

        muzzleBatchData?.Dispose();
        muzzleBatchData = null;

        impactBatchData?.Dispose();
        impactBatchData = null;

        if (muzzleFrames.IsCreated)//TODO: dont forget dispose if transfer to ShootVfxBatchData
            muzzleFrames.Dispose();
        if (muzzleIndexes.IsCreated)
            muzzleIndexes.Dispose();
        if (impactFrames.IsCreated)//TODO: dont forget dispose if transfer to ShootVfxBatchData
            impactFrames.Dispose();
        if (impactIndexes.IsCreated)
            impactIndexes.Dispose();

        mBRG?.Dispose();
    }

    public void Init(HashSet<CreepStats> uniqCreepStats)
    {
        //Debug.Log($"Use Constant Buffer: {UseConstantBuffer}");

        sharedRenderDatas = new();

        var renderDataHolder = GameServices.Instance.RenderDataHolder;
        Mesh mesh = renderDataHolder.Quad;

        mBRG = new BatchRendererGroup(this.OnPerformCulling, IntPtr.Zero);

        hpBarBatchData ??= new HpBarBatchData(renderDataHolder.HpBarMaterial, mesh, mBRG, 1000);

        warningBatchData ??= new BatchData(renderDataHolder.WarningMaterial, mesh, mBRG, 1001);

        stunBatchData ??= new BatchData(renderDataHolder.StunMaterial, mesh, mBRG, 1001);

        fearBatchData ??= new BatchData(renderDataHolder.FearMaterial, mesh, mBRG, 1001);

        foreach (CreepStats creepStats in uniqCreepStats)
            if (!sharedRenderDatas.ContainsKey(creepStats.CreepType))
                sharedRenderDatas.Add(creepStats.CreepType, RegisterCreepRenderStats(creepStats.RenderStats, creepStats.CreepType));

        int shootVfxSortingOrder = renderDataHolder.MuzzlesRenderStats.SortingOrder;
        muzzleBatchData ??= new ShootVfxBatchData(renderDataHolder.MuzzlesRenderStats.MuzzlesMaterial, mesh, mBRG, shootVfxSortingOrder);
        muzzleFrames = new NativeArray<ShootVfxAnimationFrameData>(renderDataHolder.MuzzlesRenderStats.MuzzleRenderFrameDatasDefault.ToArray(), Allocator.Persistent);
        muzzleIndexes = new NativeArray<int>(renderDataHolder.MuzzlesRenderStats.MuzzleTowerIndexes.ToArray(), Allocator.Persistent);

        impactBatchData ??= new ShootVfxBatchData(renderDataHolder.MuzzlesRenderStats.ImpactsMaterial, mesh, mBRG, shootVfxSortingOrder);
        impactFrames = new NativeArray<ShootVfxAnimationFrameData>(renderDataHolder.MuzzlesRenderStats.ImpactRenderFrameDatasDefault.ToArray(), Allocator.Persistent);
        impactIndexes = new NativeArray<int>(renderDataHolder.MuzzlesRenderStats.ImpactTowerIndexes.ToArray(), Allocator.Persistent);

        maxTowerIdCount = Enum.GetNames(typeof(AllEnums.TowerId)).Length;
    }

    // this method called every frame for render
    //[BurstCompile]
    public unsafe JobHandle OnPerformCulling(
        BatchRendererGroup rendererGroup,
        BatchCullingContext cullingContext,
        BatchCullingOutput cullingOutput,
        IntPtr userContext)
    {
        // this method called for lights too, we don't need it
        if (cullingContext.viewType != BatchCullingViewType.Camera) return new JobHandle();

        int totalCreepsCount = creepsQuery.CalculateEntityCount();
        //Debug.Log(totalCreepsCount);

        int totalMuzzleEventCount = muzzleTimedEventQuery.CalculateEntityCount();
        int totalImpactEventCount = impactTimedEventQuery.CalculateEntityCount();

        if ((totalCreepsCount == 0 || hpBarBatchData == null)
            && totalMuzzleEventCount == 0 && totalImpactEventCount == 0)
            return new JobHandle();

        EntityManager.GetAllUniqueSharedComponents(out NativeList<SharedRenderData> sharedRenderDatas, Allocator.Temp);

        int hpBarsCount = hpQuery.CalculateEntityCount();

        int drawCommandsCount;
        if (UseConstantBuffer)
        {
            drawCommandsCount = 0;
            for (int i = 1; i < sharedRenderDatas.Length; i++)
            {
                CreepBatchData batchData = batchDatas.Find(x => x.CreepType == sharedRenderDatas[i].CreepType);
                if (batchData == null) continue;

                creepsQuery.SetSharedComponentFilter(sharedRenderDatas[i]);
                drawCommandsCount += batchData.GetCommandsCount(creepsQuery.CalculateEntityCount());
            }
        }
        else
        {
            drawCommandsCount = sharedRenderDatas.Length - 1; // because sharedRenderDatas[0] has default value
        }

        drawCommandsCount += hpBarBatchData.GetCommandsCount(hpBarsCount);              //for hpBars
        drawCommandsCount += warningBatchData.GetCommandsCount(totalCreepsCount);       //for warnings
        drawCommandsCount += stunBatchData.GetCommandsCount(totalCreepsCount);          //for stun
        drawCommandsCount += fearBatchData.GetCommandsCount(totalCreepsCount);          //for fear
        drawCommandsCount += muzzleBatchData.GetCommandsCount(totalMuzzleEventCount);   //for muzzles
        drawCommandsCount += impactBatchData.GetCommandsCount(totalImpactEventCount);   //for impacts


        int visibleInstancesCount = totalCreepsCount * 3; //creeps + warnings + (stun or fear)
        visibleInstancesCount += hpBarsCount;
        visibleInstancesCount += totalMuzzleEventCount;
        visibleInstancesCount += totalImpactEventCount;

        // some magic allocations for drawCommands (just Unity example code)
        BatchCullingOutputDrawCommands* drawCommands = AllocateMemory(cullingOutput, visibleInstancesCount, drawCommandsCount);

        // Configure the single draw range to cover the single draw command which
        // is at offset 0.
        drawCommands->drawRanges[0].drawCommandsBegin = 0;
        drawCommands->drawRanges[0].drawCommandsCount = (uint)drawCommandsCount;

        // This example doesn't care about shadows or motion vectors, so it leaves everything
        // at the default zero values, except the renderingLayerMask which it sets to all ones
        // so Unity renders the instances regardless of mask settings.
        drawCommands->drawRanges[0].filterSettings = new BatchFilterSettings { renderingLayerMask = 0xffffffff, };
        //drawCommands->drawRanges[0].filterSettings = new BatchFilterSettings { renderingLayerMask = 1 };

        int drawCommandIndex = 0;
        int visibleOffset = 0;

        int hpBarIndex = 0;
        NativeArray<float> hpBufferArray = hpBarBatchData.LockGPUArray(hpBarsCount, out NativeArray<bool> isHpVisible);

        int debuffIndex = 0;
        NativeArray<float> warningBufferArray = warningBatchData.LockGPUArray(totalCreepsCount, out NativeArray<bool> isWarningVisible);
        NativeArray<float> stunBufferArray = stunBatchData.LockGPUArray(totalCreepsCount, out NativeArray<bool> isStunVisible);
        NativeArray<float> fearBufferArray = fearBatchData.LockGPUArray(totalCreepsCount, out NativeArray<bool> isFearVisible);

        NativeArray<float> muzzleBufferArray = muzzleBatchData.LockGPUArray(totalMuzzleEventCount);
        NativeArray<float> impactBufferArray = impactBatchData.LockGPUArray(totalImpactEventCount);

        // iteration through batchDatas to create draw commands for particular batch
        for (int i = 1; i < sharedRenderDatas.Length; i++)
        {
            #region Creeps
            CreepBatchData batchData = batchDatas.Find(x => x.CreepType == sharedRenderDatas[i].CreepType);

            if (batchData == null)
            {
                Debug.Log("Can't find BatchData for " + sharedRenderDatas[i].CreepType);
                //batchData = errorBatchData;
                //FillDrawCommand(drawCommands, drawCommandIndex, 0, batchDatas[0].BatchID, batchDatas[0].MaterialID, batchDatas[0].MeshID, (uint)visibleOffset, batchDatas[0].SortingOrder);
                //drawCommandIndex++;
                continue;
            }

            creepsQuery.SetSharedComponentFilter(sharedRenderDatas[i]);

            int creepEntitiesCount = creepsQuery.CalculateEntityCount();
            // Getting Direct array on GPU (or smth like it) with needed size
            NativeArray<float> creepBufferArray = batchData.LockGPUArray(creepEntitiesCount);

            // configuration drawCommands (just Unity example code)
            // we set what batch, how many units, what material and mesh for this command
            CalculateCreepRederData calculateCreepRederData = new CalculateCreepRederData()
            {
                AnimationComponents = creepsQuery.ToComponentDataArray<AnimationComponent>(Allocator.TempJob),
                Positions = creepsQuery.ToComponentDataArray<PositionComponent>(Allocator.TempJob),
                RunFrameDatas = batchData.AnimationTableRun,
                DieFrameDatas = batchData.AnimationTableDeath,
                CreepDataBuffer = creepBufferArray,
                IndexAddressObjectToWorld = batchData.ByteAddressObjectToWorld / sizeof(float),
                IndexAddressWorldToObject = batchData.ByteAddressWorldToObject / sizeof(float),
                IndexAddressColor = batchData.ByteAddressColor / sizeof(float),
                IndexAddressUV = batchData.ByteAddressUV / sizeof(float),
                IndexAddressBlink = batchData.ByteAddressBlink / sizeof(float),
                IndexAddressOutline = batchData.ByteAddressOutline / sizeof(float),
                Scale = sharedRenderDatas[i].Scale
            };
            // holding all jobs in one handle
            combinedHandle = JobHandle.CombineDependencies(combinedHandle, calculateCreepRederData.Schedule(creepEntitiesCount, 64));
            batchData.CreateDrawCommands(drawCommands, ref drawCommandIndex, creepEntitiesCount, ref visibleOffset);
            #endregion

            #region debuffs
            CalculateDebuffs calculateDebuffs = new CalculateDebuffs()
            {
                WarningDataBuffer = warningBufferArray,
                IsWarningVisible = isWarningVisible,

                StunDataBuffer = stunBufferArray,
                IsStunVisible = isStunVisible,

                FearDataBuffer = fearBufferArray,
                IsFearVisible = isFearVisible,

                Movables = creepsQuery.ToComponentDataArray<Movable>(Allocator.TempJob),
                Positions = creepsQuery.ToComponentDataArray<PositionComponent>(Allocator.TempJob),
                Stuns = creepsQuery.ToComponentDataArray<StunComponent>(Allocator.TempJob),
                Fears = creepsQuery.ToComponentDataArray<FearComponent>(Allocator.TempJob),
                Animation = creepsQuery.ToComponentDataArray<AnimationComponent>(Allocator.TempJob),

                IndexAddressObjectToWorld = warningBatchData.ByteAddressObjectToWorld / sizeof(float),
                IndexAddressWorldToObject = warningBatchData.ByteAddressWorldToObject / sizeof(float),
                StartIndex = debuffIndex,
                HpBarOffset = sharedRenderDatas[i].HpBarOffset
            };
            combinedHandle = JobHandle.CombineDependencies(combinedHandle, calculateDebuffs.Schedule(creepEntitiesCount, 64));
            debuffIndex += creepEntitiesCount;
            #endregion

            #region HP bars
            hpQuery.SetSharedComponentFilter(sharedRenderDatas[i]);
            int hpEntitiesCount = hpQuery.CalculateEntityCount();

            CalculateHpBars calculateHpBars = new CalculateHpBars()
            {
                IsHpVisible = isHpVisible,
                Creeps = hpQuery.ToComponentDataArray<CreepComponent>(Allocator.TempJob),
                Positions = hpQuery.ToComponentDataArray<PositionComponent>(Allocator.TempJob),
                DataBuffer = hpBufferArray,
                IndexAddressObjectToWorld = hpBarBatchData.ByteAddressObjectToWorld / sizeof(float),
                IndexAddressWorldToObject = hpBarBatchData.ByteAddressWorldToObject / sizeof(float),
                IndexAddressHealth = hpBarBatchData.ByteAddressHealth / sizeof(float),
                HpBarOffset = sharedRenderDatas[i].HpBarOffset,
                HpBarWidth = sharedRenderDatas[i].HpBarWidth,
                StartIndex = hpBarIndex
            };

            combinedHandle = JobHandle.CombineDependencies(combinedHandle, calculateHpBars.Schedule(hpEntitiesCount, 64));
            hpBarIndex += hpEntitiesCount;
            #endregion
        }

        #region Muzzles
        CalculateMuzzles calculateMuzzles = new CalculateMuzzles()
        {
            MuzzleFrames = muzzleFrames,
            MuzzleIndexes = muzzleIndexes,
            MaxTowerIdCount = Enum.GetNames(typeof(AllEnums.TowerId)).Length,
            MuzzleTimedEvents = muzzleTimedEventQuery.ToComponentDataArray<MuzzleTimedEvent>(Allocator.TempJob),
            MuzzleDataBuffer = muzzleBufferArray,
            IndexAddressUV = muzzleBatchData.ByteAdressMazzleUV / sizeof(float),
            IndexAddressObjectToWorld = muzzleBatchData.ByteAddressObjectToWorld / sizeof(float),
            IndexAddressWorldToObject = muzzleBatchData.ByteAddressWorldToObject / sizeof(float)
        };

        combinedHandle = JobHandle.CombineDependencies(combinedHandle, calculateMuzzles.Schedule(totalMuzzleEventCount, 64));
        #endregion

        #region Impacts
        CalculateImpacts calculateImpacts = new CalculateImpacts()
        {
            ImpactFrames = impactFrames,
            ImpactIndexes = impactIndexes,
            MaxTowerIdCount = Enum.GetNames(typeof(AllEnums.TowerId)).Length,
            ImpactTimedEvents = impactTimedEventQuery.ToComponentDataArray<ImpactTimedEvent>(Allocator.TempJob),
            ImpactDataBuffer = impactBufferArray,
            IndexAddressUV = impactBatchData.ByteAdressMazzleUV / sizeof(float),
            IndexAddressObjectToWorld = impactBatchData.ByteAddressObjectToWorld / sizeof(float),
            IndexAddressWorldToObject = impactBatchData.ByteAddressWorldToObject / sizeof(float)
        };

        combinedHandle = JobHandle.CombineDependencies(combinedHandle, calculateImpacts.Schedule(totalImpactEventCount, 64));
        #endregion

        //-------------------------------------------------//
        //-------------------------------------------------//
        // comlete all jobs of writing to Direct GPU array //
        combinedHandle.Complete();
        //-------------------------------------------------//
        //-------------------------------------------------//

        sharedRenderDatas.Dispose();
        creepsQuery.ResetFilter();
        hpQuery.ResetFilter();

        for (int i = 0; i < batchDatas.Count; i++)
        {
            if (batchDatas[i].IsLockedForWrite)
                batchDatas[i].UnlockGPUArray();
        }

        hpBarBatchData.CreateDrawCommands(drawCommands, ref drawCommandIndex, hpBarsCount, ref visibleOffset);
        warningBatchData.CreateDrawCommands(drawCommands, ref drawCommandIndex, totalCreepsCount, ref visibleOffset);
        stunBatchData.CreateDrawCommands(drawCommands, ref drawCommandIndex, totalCreepsCount, ref visibleOffset);
        fearBatchData.CreateDrawCommands(drawCommands, ref drawCommandIndex, totalCreepsCount, ref visibleOffset);
        muzzleBatchData.CreateDrawCommands(drawCommands, ref drawCommandIndex, totalMuzzleEventCount, ref visibleOffset);
        impactBatchData.CreateDrawCommands(drawCommands, ref drawCommandIndex, totalImpactEventCount, ref visibleOffset);

        hpBarBatchData.UnlockGPUArray();
        warningBatchData.UnlockGPUArray();
        stunBatchData.UnlockGPUArray();
        fearBatchData.UnlockGPUArray();
        muzzleBatchData.UnlockGPUArray();
        impactBatchData.UnlockGPUArray();

        // This simple example doesn't use jobs, so it returns an empty JobHandle.
        // Performance-sensitive applications are encouraged to use Burst jobs to implement
        // culling and draw command output. In this case, this function returns a
        // handle here that completes when the Burst jobs finish.
        return new JobHandle();
    }

    protected override void OnUpdate()
    {
        if (newCreepsQuery.IsEmpty) return;

        EntityManager.GetAllUniqueSharedComponents(out NativeList<SharedCreepData> sharedCreepDatas, Allocator.Temp);

        TimeSkipper timeSkipper = SystemAPI.GetSingleton<TimeSkipper>();

        for (int i = 1; i < sharedCreepDatas.Length; i++)
        {
            SharedCreepData data = sharedCreepDatas[i];
            newCreepsQuery.SetSharedComponentFilter(data);

            NativeArray<Entity> entities = newCreepsQuery.ToEntityArray(Allocator.Temp);
            NativeArray<CreepComponent> creepComponents = newCreepsQuery.ToComponentDataArray<CreepComponent>(Allocator.Temp);
            NativeArray<PositionComponent> positionComponents = newCreepsQuery.ToComponentDataArray<PositionComponent>(Allocator.Temp);
            SharedRenderData renderData = sharedRenderDatas[data.CreepType];

            for (int k = 0; k < entities.Length; k++)
            {
                EntityManager.AddSharedComponent(entities[k], renderData);

                EntityManager.AddComponentData(entities[k],
                    new AnimationComponent()
                    {
                        FrameNumber = (byte)UnityEngine.Random.Range(0, renderData.RunFrames),
                        Direction = positionComponents[k].Direction,
                        AnimationState = AllEnums.AnimationState.Run,
                        AnimationTimer = 0,
                        DamageTimer = 0,
                        DamageTaken = false,
                        Color = new float4(1, 1, 1, 1),
                        IsOutline = ((creepComponents[k].WaveNumber + 1) % 5 == 0) || (creepComponents[k].WaveNumber + 1 == timeSkipper.WavesCount)
                    });
            }
            positionComponents.Dispose();
            creepComponents.Dispose();
            entities.Dispose();
        }

        newCreepsQuery.ResetFilter();
    }

    #region jobs
    // Job of writing needed data to direct GPU array (buffer) 
    [BurstCompile]
    private struct CalculateCreepRederData : IJobParallelFor
    {
        [ReadOnly] public NativeArray<AnimationFrameData> RunFrameDatas;
        [ReadOnly] public NativeArray<AnimationFrameData> DieFrameDatas;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<AnimationComponent> AnimationComponents;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<PositionComponent> Positions;

        // pointers to data in indexes (not in bytes)
        [ReadOnly] public int IndexAddressObjectToWorld;
        [ReadOnly] public int IndexAddressWorldToObject;
        [ReadOnly] public int IndexAddressColor;
        [ReadOnly] public int IndexAddressUV;
        [ReadOnly] public int IndexAddressBlink;
        [ReadOnly] public int IndexAddressOutline;

        [ReadOnly] public float Scale;

        // Direct array on GPU (or smth like it)
        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<float> CreepDataBuffer;

        public void Execute(int i)
        {
            AnimationFrameData frameData;
            quaternion rotation;
            if (AnimationComponents[i].AnimationState == AllEnums.AnimationState.Death)
            {
                rotation = quaternion.identity;
                frameData = DieFrameDatas[AnimationComponents[i].FrameNumber];
            }
            else
            {
                //Debug.Log($"frame num: {AnimationComponents[i].FrameNumber}");
                rotation = quaternion.Euler(new float3(0, 0, Utilities.SignedAngleBetween(new float2(0, 1), AnimationComponents[i].Direction)));
                frameData = RunFrameDatas[AnimationComponents[i].FrameNumber];
            }

            // calculate matrix of transform, rotation and scale it's ObjectToWorld matrix
            // quaternion.identity - because we use quad without rotation, with different sprites (holding rotation in sprites)
            float4x4 matr = float4x4.TRS(GetPosition(Positions[i].Position, frameData.PositionOffset),
                rotation,
                GetScale(Scale, frameData.Scale));

            // write to buffer ObjectToWorld matrix in right address
            FillDataBuffer(ref CreepDataBuffer, IndexAddressObjectToWorld, i, matr);

            // calculation WorldToObject matrix, it's inverse ObjectToWorld matrix 
            float4x4 inverse = math.inverse(matr);
            // write to buffer WorldToObject matrix in right address
            FillDataBuffer(ref CreepDataBuffer, IndexAddressWorldToObject, i, inverse);

            // write to buffer Color in right address
            FillDataBuffer(ref CreepDataBuffer, IndexAddressColor, i, AnimationComponents[i].Color);

            // write to buffer UV in right address
            FillDataBuffer(ref CreepDataBuffer, IndexAddressUV, i, frameData.UV);

            // write to buffer Blink in right address
            if (AnimationComponents[i].DamageTaken)
                CreepDataBuffer[IndexAddressBlink + i + 0] = 0;
            else
                CreepDataBuffer[IndexAddressBlink + i + 0] = AnimationComponents[i].DamageTimer;

            // write to buffer Outline in right address
            CreepDataBuffer[IndexAddressOutline + i + 0] = AnimationComponents[i].IsOutline ? 1 : 0;
        }
    }

    [BurstCompile]
    private struct CalculateHpBars : IJobParallelFor
    {
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<CreepComponent> Creeps;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<PositionComponent> Positions;

        [ReadOnly] public int IndexAddressObjectToWorld;
        [ReadOnly] public int IndexAddressWorldToObject;
        [ReadOnly] public int IndexAddressHealth;

        [ReadOnly] public float HpBarOffset;
        [ReadOnly] public float HpBarWidth;

        [ReadOnly] public int StartIndex;

        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<float> DataBuffer;
        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<bool> IsHpVisible;

        public void Execute(int i)
        {
            bool isVisible = (Creeps[i].Hp != Creeps[i].MaxHp) && !Creeps[i].Escaped;
            IsHpVisible[i + StartIndex] = isVisible;
            if (!isVisible)
                return;

            float4x4 matr = float4x4.TRS(new float3(Positions[i].Position.x, Positions[i].Position.y + HpBarOffset, 0),
                quaternion.identity,
                new float3(HpBarWidth, .07f, 1));

            // write to buffer ObjectToWorld matrix in right address
            FillDataBuffer(ref DataBuffer, IndexAddressObjectToWorld, (i + StartIndex), matr);

            // calculation WorldToObject matrix, it's inverse ObjectToWorld matrix 
            float4x4 inverse = math.inverse(matr);
            // write to buffer WorldToObject matrix in right address
            FillDataBuffer(ref DataBuffer, IndexAddressWorldToObject, (i + StartIndex), inverse);

            // write to buffer Health in right address
            DataBuffer[IndexAddressHealth + (i + StartIndex) + 0] = Creeps[i].Hp / Creeps[i].MaxHp;
        }
    }

    [BurstCompile]
    private struct CalculateDebuffs : IJobParallelFor
    {
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<Movable> Movables;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<StunComponent> Stuns;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<FearComponent> Fears;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<PositionComponent> Positions;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<AnimationComponent> Animation;

        [ReadOnly] public int IndexAddressObjectToWorld;
        [ReadOnly] public int IndexAddressWorldToObject;

        [ReadOnly] public float HpBarOffset;

        [ReadOnly] public int StartIndex;

        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<float> WarningDataBuffer;
        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<bool> IsWarningVisible;

        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<float> StunDataBuffer;
        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<bool> IsStunVisible;

        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<float> FearDataBuffer;
        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<bool> IsFearVisible;

        public void Execute(int i)
        {
            if (Animation[i].AnimationState == AllEnums.AnimationState.Death)
            {
                IsWarningVisible[i + StartIndex] = false;
                IsStunVisible[i + StartIndex] = false;
                IsFearVisible[i + StartIndex] = false;
                return;
            }

            bool isVisible;
            isVisible = Stuns[i].Time > 0;
            IsStunVisible[i + StartIndex] = isVisible;
            if (isVisible)
            {
                //(1, .865f)
                FillDataBuffer(new float3(.6f, .519f, 1), HpBarOffset + .3f, ref StunDataBuffer, i);
                IsWarningVisible[i + StartIndex] = false;
            }
            else
            {
                isVisible = !Movables[i].IsGoingIn;
                IsWarningVisible[i + StartIndex] = isVisible;
                if (isVisible)
                    // (1, .923f)
                    FillDataBuffer(new float3(.6f, .5538f, 1), HpBarOffset + .4f, ref WarningDataBuffer, i);
            }

            isVisible = Fears[i].Time > 0;
            IsFearVisible[i + StartIndex] = isVisible;
            if (isVisible)
                // (.948f, 1)
                FillDataBuffer(new float3(.474f, .5f, 1), HpBarOffset + .3f, ref FearDataBuffer, i);
        }
        private void FillDataBuffer(float3 scale, float debuffOffset, ref NativeArray<float> buffer, int i)
        {
            float4x4 matr = float4x4.TRS(new float3(Positions[i].Position.x, Positions[i].Position.y + debuffOffset, 0),
                            quaternion.identity,
                            scale);
            // write to buffer ObjectToWorld matrix in right address
            BRGSystem.FillDataBuffer(ref buffer, IndexAddressObjectToWorld, (i + StartIndex), matr);
            // calculation WorldToObject matrix, it's inverse ObjectToWorld matrix 
            float4x4 inverse = math.inverse(matr);
            // write to buffer WorldToObject matrix in right address
            BRGSystem.FillDataBuffer(ref buffer, IndexAddressWorldToObject, (i + StartIndex), inverse);
        }
    }

    [BurstCompile]
    private struct CalculateMuzzles : IJobParallelFor
    {
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<MuzzleTimedEvent> MuzzleTimedEvents;

        [ReadOnly] public int IndexAddressObjectToWorld;
        [ReadOnly] public int IndexAddressWorldToObject;
        [ReadOnly] public int IndexAddressUV;

        [ReadOnly] public NativeArray<ShootVfxAnimationFrameData> MuzzleFrames;
        [ReadOnly] public NativeArray<int> MuzzleIndexes;
        [ReadOnly] public int MaxTowerIdCount;

        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<float> MuzzleDataBuffer;

        public void Execute(int i)
        {
            int towerIndex = 2 * (Utilities.TowerIdToInt(MuzzleTimedEvents[i].TowerId) - 1);
            towerIndex += MuzzleTimedEvents[i].IsEnhanced ? 2 * MaxTowerIdCount : 0;
            int index = MuzzleIndexes[towerIndex] + MuzzleTimedEvents[i].CurrentFrame;

            float4x4 matr = float4x4.TRS(GetPosition2(MuzzleTimedEvents[i].Position, MuzzleFrames[index].PositionOffset * MuzzleFrames[index].ScaleModifier, MuzzleTimedEvents[i].Direction),
                            quaternion.Euler(new float3(0, 0, Utilities.SignedAngleBetween(new float2(0, 1), new(-MuzzleTimedEvents[i].Direction.y, MuzzleTimedEvents[i].Direction.x)))),
                            GetScale(MuzzleFrames[index].ScaleModifier, MuzzleFrames[index].Scale));
            // write to buffer ObjectToWorld matrix in right address
            BRGSystem.FillDataBuffer(ref MuzzleDataBuffer, IndexAddressObjectToWorld, i, matr);
            // calculation WorldToObject matrix, it's inverse ObjectToWorld matrix 
            float4x4 inverse = math.inverse(matr);
            // write to buffer WorldToObject matrix in right address
            BRGSystem.FillDataBuffer(ref MuzzleDataBuffer, IndexAddressWorldToObject, i, inverse);
            // create UV
            float4 uv = MuzzleFrames[index].UV;
            BRGSystem.FillDataBuffer(ref MuzzleDataBuffer, IndexAddressUV, i, uv);

            float3 GetPosition2(float2 position, float2 offset, float2 dir) => (position + offset.x * dir + offset.y * new float2(-dir.y, dir.x)).ToFloat3();
        }
    }

    [BurstCompile]
    private struct CalculateImpacts : IJobParallelFor
    {
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<ImpactTimedEvent> ImpactTimedEvents;

        [ReadOnly] public int IndexAddressObjectToWorld;
        [ReadOnly] public int IndexAddressWorldToObject;
        [ReadOnly] public int IndexAddressUV;

        [ReadOnly] public NativeArray<ShootVfxAnimationFrameData> ImpactFrames;
        [ReadOnly] public NativeArray<int> ImpactIndexes;
        [ReadOnly] public int MaxTowerIdCount;

        [WriteOnly, NativeDisableContainerSafetyRestriction]
        public NativeArray<float> ImpactDataBuffer;

        public void Execute(int i)
        {
            int towerIndex = 2 * (Utilities.TowerIdToInt(ImpactTimedEvents[i].TowerId) - 1);
            towerIndex += ImpactTimedEvents[i].IsEnhanced ? 2 * MaxTowerIdCount : 0;
            int index = ImpactIndexes[towerIndex] + ImpactTimedEvents[i].CurrentFrame;
            float scaleModifier = ImpactFrames[index].ScaleModifier;
            scaleModifier *= ImpactTimedEvents[i].AoeScale == 0 ? 1 : ImpactTimedEvents[i].AoeScale;

            float4x4 matr = float4x4.TRS(GetPosition2(ImpactTimedEvents[i].Position, ImpactFrames[index].PositionOffset * ImpactFrames[index].ScaleModifier, ImpactTimedEvents[i].Direction),
                            quaternion.Euler(new float3(0, 0, Utilities.SignedAngleBetween(new float2(0, 1), new(-ImpactTimedEvents[i].Direction.y, ImpactTimedEvents[i].Direction.x)))),
                            GetScale(scaleModifier, ImpactFrames[index].Scale));
            // write to buffer ObjectToWorld matrix in right address
            BRGSystem.FillDataBuffer(ref ImpactDataBuffer, IndexAddressObjectToWorld, i, matr);
            // calculation WorldToObject matrix, it's inverse ObjectToWorld matrix 
            float4x4 inverse = math.inverse(matr);
            // write to buffer WorldToObject matrix in right address
            BRGSystem.FillDataBuffer(ref ImpactDataBuffer, IndexAddressWorldToObject, i, inverse);
            // create UV
            float4 uv = ImpactFrames[index].UV;
            BRGSystem.FillDataBuffer(ref ImpactDataBuffer, IndexAddressUV, i, uv);

            float3 GetPosition2(float2 position, float2 offset, float2 dir) => (position + offset.x * dir + offset.y * new float2(-dir.y, dir.x)).ToFloat3();
        }
    }
    #endregion

    private SharedRenderData RegisterCreepRenderStats(CreepRenderStats creepRenderStats, AllEnums.CreepType creepType)
    {
        SharedRenderData result;

        CreepBatchData data = new CreepBatchData
        (
            creepRenderStats.CreepMaterial,
            GameServices.Instance.RenderDataHolder.Quad,
            mBRG,
            creepType,
            new NativeArray<AnimationFrameData>(creepRenderStats.AnimationTableRun.ToArray(), Allocator.Persistent),
            new NativeArray<AnimationFrameData>(creepRenderStats.AnimationTableDie.ToArray(), Allocator.Persistent),
            creepRenderStats.SortingOrder
        );

        batchDatas.Add(data);

        result = new SharedRenderData()
        {
            DeathColor = new float3(creepRenderStats.DeathColor.r, creepRenderStats.DeathColor.g, creepRenderStats.DeathColor.b),
            CreepType = creepType,
            Scale = creepRenderStats.Scale,
            RunFrames = (byte)creepRenderStats.RunFrames,
            DieFrames = (byte)creepRenderStats.DieFrames,
            HpBarOffset = creepRenderStats.HpBarOffset,
            HpBarWidth = creepRenderStats.HpBarWidth,
            TimeBetweenRunFrames = creepRenderStats.TimeBetweenRunFrames,
            TimeBetweenDieFrames = creepRenderStats.TimeBetweenDieFrames,
        };

        //Debug.Log("----> created new shared render data for " + result.CreepType);

        return result;
    }

#if UNITY_EDITOR
    public void ShowBatchData()
    {
        if (batchDatas.Count == 0)
            Debug.Log("BatchDatas is empty");
        else
        {
            for (int i = 0; i < batchDatas.Count; i++)
                Debug.Log(batchDatas[i].CreepType);
        }
    }
#endif
}