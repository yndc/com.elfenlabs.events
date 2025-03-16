using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Elfenlabs.Events
{
    /// <summary>
    /// A special system that resets all event buffer on update
    /// </summary>
    [UpdateInGroup(typeof(EventHandlerSystemGroup), OrderLast = true)]
    public partial struct EventMaintenanceSystem : ISystem
    {
        EntityQuery m_EntityBufferQuery;
        ComponentTypeHandle<EventBuffer> m_EventBufferTypeHandle;

        void OnCreate(ref SystemState state)
        {
            m_EntityBufferQuery = state.GetEntityQuery(ComponentType.ReadOnly<EventBuffer>());
            m_EventBufferTypeHandle = state.GetComponentTypeHandle<EventBuffer>(true);
        }

        [BurstCompile(DisableSafetyChecks = true)] // Safety checks disabled to be able to reset the buffers without signalling a change
        void OnUpdate(ref SystemState state)
        {
            m_EventBufferTypeHandle.Update(ref state);
            var chunks = m_EntityBufferQuery.ToArchetypeChunkArray(Allocator.TempJob);
            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                var eventContainerTypeHandle = chunk.GetChunkComponentData(ref m_EventBufferTypeHandle).BufferTypeHandle;
                eventContainerTypeHandle.Update(ref state);
                if (chunk.DidChange(ref eventContainerTypeHandle, state.LastSystemVersion))
                {
                    var untypedBufferAccessor = chunk.GetUntypedBufferAccessor(ref eventContainerTypeHandle);
                    for (int entityIndex = 0; entityIndex < untypedBufferAccessor.Length; entityIndex++)
                    {
                        untypedBufferAccessor.ResizeUninitialized(entityIndex, 0);
                    }
                }
            }
            chunks.Dispose();
        }
    }
}