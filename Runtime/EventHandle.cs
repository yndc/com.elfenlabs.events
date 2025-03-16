using Unity.Entities;
using Unity.Collections;
using Unity.Jobs.LowLevel.Unsafe;
using System.Collections.Generic;
using System.Collections;
using Unity.Jobs;
using Unity.Burst;

namespace Elfenlabs.Events
{
    /// <summary>
    /// A thin wrapper for querying and writing event buffers
    /// </summary>
    /// <typeparam name="E"></typeparam>
    [BurstCompile]
    public partial struct EventHandle<E> where E : unmanaged, IEventData
    {
        EntityQuery m_BufferEntityQueryRW; // Write to any event buffers
        EntityQuery m_BufferEntityQueryChangedRW; // Write non-empty event buffers
        EntityQuery m_BufferEntityQueryChangedRO; // Read non-empty event buffers
        BufferTypeHandle<E> m_BufferTypeHandleRO;
        BufferTypeHandle<E> m_BufferTypeHandleRW;
        ArchetypeChunk m_Chunk;

        /// <summary>
        /// Returns true if there is no event to process in this frame
        /// </summary>
        public bool IsEmpty
        {
            get => m_BufferEntityQueryChangedRO.IsEmpty;
        }

        /// <summary>
        /// Create a new event container, if the buffer entities does not exists, they will be created
        /// </summary>
        /// <param name="state"></param>
        //public EventHandle(ref SystemState state) : this(state.EntityManager) { }

        /// <summary>
        /// Create a new event container, if the buffer entities does not exists, they will be created
        /// </summary>
        /// <param name="entityManager"></param>
        public EventHandle(ref SystemState state)
        {
            m_BufferEntityQueryRW = state.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAllRW<E>());
            m_BufferEntityQueryChangedRO = new EntityQueryBuilder(Allocator.Temp).WithAll<E>().Build(ref state);
            m_BufferEntityQueryChangedRO.SetChangedVersionFilter(ComponentType.ReadOnly<E>());
            m_BufferEntityQueryChangedRW = state.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAllRW<E>());
            m_BufferEntityQueryChangedRW.SetChangedVersionFilter(ComponentType.ReadOnly<E>());
            m_BufferTypeHandleRO = state.GetBufferTypeHandle<E>(true);
            m_BufferTypeHandleRW = state.GetBufferTypeHandle<E>();
            if (m_BufferEntityQueryChangedRW.IsEmpty)
            {
                var archetypeComponents = new NativeArray<ComponentType>(2, Allocator.Temp);
                archetypeComponents[0] = ComponentType.ReadWrite<EventBuffer>();
                archetypeComponents[1] = ComponentType.ReadWrite<E>();
                var archetype = state.EntityManager.CreateArchetype(archetypeComponents);
                archetypeComponents.Dispose();
                for (int i = 0; i < JobsUtility.ThreadIndexCount; i++)
                {
                    var entity = state.EntityManager.CreateEntity(archetype);
                    state.EntityManager.SetName(entity, string.Format("{0}[{1}]", typeof(E).Name, i));
                }
                state.EntityManager.AddChunkComponentData(m_BufferEntityQueryChangedRW, new EventBuffer
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    BufferTypeHandle = state.GetDynamicComponentTypeHandle(ComponentType.ReadWrite<E>())
#else
                    BufferTypeHandle = state.GetDynamicComponentTypeHandle(ComponentType.ReadOnly<E>())
#endif
                });
            }
            using var tempArray = m_BufferEntityQueryRW.ToArchetypeChunkArray(Allocator.Temp);
            m_Chunk = tempArray[0];
        }

        /// <summary>
        /// Update the event type handle and dynamic buffer references
        /// </summary>
        /// <param name="state"></param>
        public void Update(ref SystemState state, bool readOnly = false)
        {
            m_BufferTypeHandleRO.Update(ref state);
            m_BufferTypeHandleRW.Update(ref state);
        }

        /// <summary>
        /// Write an event to the buffer
        /// </summary>
        /// <param name="value"></param>
        public void Write(E value)
        {
            m_Chunk.GetBufferAccessor(ref m_BufferTypeHandleRW)[JobsUtility.ThreadIndex].Add(value);
        }

        /// <summary>
        /// Read an event from the buffer with the given index
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public E Read(int index = 0)
        {
            return Read(index, JobsUtility.ThreadIndex);
        }

        /// <summary>
        /// Read an event from the buffer with the given index
        /// </summary>
        /// <param name="index"></param>
        /// <param name="threadIndex"></param>
        /// <returns></returns>
        public E Read(int index, int threadIndex)
        {
            return m_Chunk.GetBufferAccessor(ref m_BufferTypeHandleRW)[threadIndex][index];
        }

        /// <summary>
        /// Manually clear the event buffer
        /// </summary>
        public void Clear()
        {
            var accessor = m_Chunk.GetBufferAccessor(ref m_BufferTypeHandleRW);
            for (int i = 0; i < accessor.Length; i++)
            {
                accessor[i].Clear();
            }
        }

        /// <summary>
        /// Manually clear the event buffer
        /// </summary>
        public JobHandle Clear(JobHandle dependsOn)
        {
            return new ClearJob
            {
                Chunk = m_Chunk,
                BufferTypeHandleRW = m_BufferTypeHandleRW,
            }.Schedule(dependsOn);
        }

        /// <summary>
        /// Get an enumerator for the event buffer.
        /// Only valid for the current frame. 
        /// </summary>
        /// <param name="state"></param>
        /// <param name="readOnly"></param>
        /// <returns></returns>
        public Enumerator GetEnumerator(bool readOnly = false)
        {
            return new Enumerator(m_Chunk, (readOnly ? m_BufferTypeHandleRO : m_BufferTypeHandleRW), 0);
        }

        /// <summary>
        /// Check if there is any event in the buffer, if so, return an enumerator
        /// </summary>
        /// <param name="enumerator"></param>
        /// <returns></returns>
        public bool TryGetEnumerator(out Enumerator enumerator, bool readOnly = false)
        {
            if (!IsEmpty)
            {
                enumerator = GetEnumerator(readOnly);
                return true;
            }
            enumerator = default;
            return false;
        }

        /// <summary>
        /// Get a parallel writer for the event buffer
        /// </summary>
        /// <returns></returns>
        public ParallelWriter AsParallelWriter()
        {
            return new ParallelWriter(m_Chunk, m_BufferTypeHandleRW);
        }

        /// <summary>
        /// Enumerator for the event buffer
        /// </summary>
        public struct Enumerator : IEnumerator<E>
        {
            internal BufferAccessor<E> m_BufferAccessor;
            internal ArchetypeChunk m_Chunk;
            internal int m_StartEntityIndex;
            internal int m_StartIndex;
            internal int m_StartLength;
            internal int m_CurrentEntityIndex;
            internal int m_CurrentIndex;
            internal int m_CurrentLength;
            internal int m_Count;
            internal bool m_IsAccessorInitialized;
            internal BufferTypeHandle<E> m_BufferTypeHandle;

            public Enumerator(ArchetypeChunk chunk, BufferTypeHandle<E> typeHandle, int count, int entityIndex = 0, int index = 0, int length = -1)
            {
                m_Chunk = chunk;
                m_IsAccessorInitialized = false;
                m_BufferTypeHandle = typeHandle;
                m_BufferAccessor = default;
                m_Count = count;
                m_StartEntityIndex = entityIndex;
                m_StartIndex = index;
                m_StartLength = length;
                m_CurrentEntityIndex = entityIndex;
                m_CurrentIndex = index - 1;  // Start at -1 so that MoveNext will advance to the first element
                m_CurrentLength = length + 1; // Start at length + 1 so that MoveNext will advance to the first element
            }

            public E Current
            {
                get => m_BufferAccessor[m_CurrentEntityIndex][m_CurrentIndex];
                set => m_BufferAccessor[m_CurrentEntityIndex].ElementAt(m_CurrentIndex) = value;
            }

            public int Count => m_Count;

            object IEnumerator.Current => Current;

            E IEnumerator<E>.Current => Current;

            public bool MoveNext()
            {
                AssertAccessor();
                m_CurrentLength--;
                if (m_CurrentLength == 0)
                {
                    return false;
                }
                m_CurrentIndex++;
                if (m_CurrentIndex == m_BufferAccessor[m_CurrentEntityIndex].Length)
                {
                    m_CurrentIndex = 0;

                    // Skip entities with no elements
                    do
                    {
                        m_CurrentEntityIndex++;
                    } while (m_CurrentEntityIndex < m_BufferAccessor.Length && m_BufferAccessor[m_CurrentEntityIndex].Length == 0);

                    if (m_CurrentEntityIndex == m_BufferAccessor.Length)
                    {
                        return false;
                    }
                }
                return true;
            }

            public void Reset()
            {
                m_CurrentEntityIndex = m_StartEntityIndex;
                m_CurrentIndex = m_StartIndex - 1;
                m_CurrentLength = m_StartLength + 1;
            }

            public void Dispose() { }

            void AssertAccessor()
            {
                if (!m_IsAccessorInitialized)
                {
                    m_BufferAccessor = m_Chunk.GetBufferAccessor(ref m_BufferTypeHandle);
                    m_IsAccessorInitialized = true;
                }
            }
        }

        /// <summary>
        /// Parallel writer for the event buffer
        /// </summary>
        public struct ParallelWriter
        {
            [NativeDisableParallelForRestriction] BufferAccessor<E> m_BufferAccessor;
            ArchetypeChunk m_Chunk;
            BufferTypeHandle<E> m_BufferTypeHandleRW;
            bool m_IsAccessorInitialized;

            /// <summary>
            /// Write an event to the buffer
            /// </summary>
            /// <param name="value"></param>
            public void Write(E value)
            {
                AssertAccessor();
                m_BufferAccessor[JobsUtility.ThreadIndex].Add(value);
            }

            public ParallelWriter(ArchetypeChunk chunk, BufferTypeHandle<E> bufferTypeHandle)
            {
                m_Chunk = chunk;
                m_BufferTypeHandleRW = bufferTypeHandle;
                m_BufferAccessor = default;
                m_IsAccessorInitialized = false;
            }

            void AssertAccessor()
            {
                if (!m_IsAccessorInitialized)
                {
                    m_BufferAccessor = m_Chunk.GetBufferAccessor(ref m_BufferTypeHandleRW);
                    m_IsAccessorInitialized = true;
                }
            }
        }

        [BurstCompile]
        partial struct ClearJob : IJob
        {
            public ArchetypeChunk Chunk;
            public BufferTypeHandle<E> BufferTypeHandleRW;

            public void Execute()
            {
                var accessor = Chunk.GetBufferAccessor(ref BufferTypeHandleRW);
                for (int i = 0; i < accessor.Length; i++)
                {
                    accessor[i].Clear();
                }
            }
        }
    }
}
