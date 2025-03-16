using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Elfenlabs.Events
{
    /// <summary>
    /// Interface for event data
    /// </summary>
    public interface IEventData : IBufferElementData { }

    /// <summary>
    /// Chunk component data for event buffer info
    /// </summary>
    public struct EventBuffer : IComponentData
    {
        public DynamicComponentTypeHandle BufferTypeHandle;
    }
}