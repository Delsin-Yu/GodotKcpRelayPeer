using System.Collections.Concurrent;
using KcpGameServer.Models;
using Timer = System.Timers.Timer;

namespace KcpGameServer.Managers.Pending
{
    public enum ExtractResult { InvalidKey, InternalError, Success }

    public abstract class PendingBuffer<TKey, TValue> where TKey : struct
                                                      where TValue : struct, IPendingCache
    {
        private readonly ConcurrentDictionary<TKey, TValue> m_Pending = new();

        protected PendingBuffer(CancellationToken token)
        {
            var gcTimer = new Timer(TimeSpan.FromSeconds(1));
            Stack<TKey> removeBuffer = new(); 
            gcTimer.Elapsed += (o, elapsedEventArgs) =>
            {
                foreach (var token1 in m_Pending.Keys)
                {
                    var sessionCacheModel = m_Pending[token1];
                    sessionCacheModel.LifeTime--;
                    if (sessionCacheModel.LifeTime < 0)
                    {
                        removeBuffer.Push(token1);
                    }
                    else
                    {
                        m_Pending[token1] = sessionCacheModel;
                    }
                }

                while (removeBuffer.Count > 0)
                {
                    var key = removeBuffer.Pop();
                    OnGC(key);
                    m_Pending.Remove(key, out _);
                }
            };
            token.Register(gcTimer.Dispose);
            gcTimer.Start();
        }

        protected abstract void OnGC(in TKey key);

        public bool IsPending(in TKey key) => m_Pending.ContainsKey(key);

        public ExtractResult TryExtractBuffer(in TKey key, out TValue value)
        {
            if (!m_Pending.ContainsKey(key))
            {
                value = default;
                return ExtractResult.InvalidKey;
            }

            var removeResult = m_Pending.TryRemove(key, out value);

            if (removeResult) return ExtractResult.Success;

            Console.WriteLine($"!Failed to remove key!");
            return ExtractResult.InternalError;
        }

        public bool TryAddPendingBuffer(in TKey key, in TValue value) => m_Pending.TryAdd(key, value);
    }

    public struct KcpPendingBufferLifetime(int lifeTime) : IPendingCache
    {
        public int LifeTime { get; set; } = lifeTime;
    }

    public class KcpPendingBuffer(Action<int> onTimeOutCallback, CancellationToken token) : PendingBuffer<int, KcpPendingBufferLifetime>(token)
    {
        protected override void OnGC(in int key) => onTimeOutCallback(key);
    }

    public class TokenPendingBuffer<T>(CancellationToken token) : PendingBuffer<Guid, T>(token)
        where T : struct, IPendingCache
    {
        protected override void OnGC(in Guid key) { }

        public Guid AddPendingBuffer(in T value)
        {
            Guid newGuid;
            do
            {
                newGuid = Guid.NewGuid();
            }
            while (!TryAddPendingBuffer(newGuid, value));

            return newGuid;
        }
    }
}
