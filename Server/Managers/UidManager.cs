using System.Collections.Concurrent;
using System.Collections.Generic;

namespace KcpGameServer.Models
{
    public static class UidManager
    {
        private static readonly object s_Lock = new();
        private static ulong s_Id;
        private static readonly Stack<ulong> s_ReleasedId = new();

        public static ulong? Get()
        {
            lock (s_Lock)
            {
                if (s_ReleasedId.Count > 0)
                {
                    return s_ReleasedId.Pop();
                }

                try
                {
                    return checked(s_Id++);
                }
                catch
                {
                    return null;
                }
            }
        }

        public static void Release(ulong id)
        {
            lock (s_Lock)
            {
                s_ReleasedId.Push(id);
            }
        }
    }
}