namespace KcpGameServer.Models
{
    public class SessionModel
    {
        public class BiMap
        {
            private Dictionary<int, int> ConnectionIdToLocalId { get; } = [];
            private Dictionary<int, int> LocalIdToConnectionId { get; } = [];
            
            public int Count => ConnectionIdToLocalId.Count;

            public bool HasConnectionId(int connectionId) => ConnectionIdToLocalId.ContainsKey(connectionId);
            public bool HasLocalId(int localId) => LocalIdToConnectionId.ContainsKey(localId);
            
            public bool TryGetLocalId(int connectionId, out int localId) => 
                ConnectionIdToLocalId.TryGetValue(connectionId, out localId);

            public bool TryGetConnectionId(int localId, out int connectionId) => 
                LocalIdToConnectionId.TryGetValue(localId, out connectionId);

            public IEnumerable<int> ConnectionIds => ConnectionIdToLocalId.Keys;
            public IEnumerable<int> LocalIds => LocalIdToConnectionId.Keys;
            
            public void Add(int connectionId, int localId)
            {
                ConnectionIdToLocalId.Add(connectionId, localId);
                LocalIdToConnectionId.Add(localId, connectionId);
            }

            public void Remove(int connectionId)
            {
                if (!ConnectionIdToLocalId.Remove(connectionId, out var localId)) throw new InvalidOperationException();
                if (!LocalIdToConnectionId.Remove(localId)) throw new InvalidOperationException();
            }
        }
        
        public ulong SessionId { get; }
        public int HostConnectionId { get; }
        public string SessionName { get; private set; }
        public int MaxMemberCount { get; private set; }

        public BiMap ConnectionMap { get; private set; } = new();

        public void ModifySessionInfo(in SessionInfoModel sessionModifyModel) => (SessionName, MaxMemberCount) = sessionModifyModel;

        private SessionModel(ulong sessionId, int hostConnectionId, in SessionInfoModel sessionInfoModel)
        {
            SessionId = sessionId;
            HostConnectionId = hostConnectionId;
            (SessionName, MaxMemberCount) = sessionInfoModel;
        }

        public bool IsSessionFull(out int currentCount)
        {
            currentCount = ConnectionMap.Count;
            return currentCount >= MaxMemberCount;
        }

        
        
        public static SessionModel FromInfoModel(ulong sessionId, int hostConnectionId, in SessionInfoModel sessionInfoModel)
        {
            var model = new SessionModel(sessionId, hostConnectionId, sessionInfoModel);
            model.ConnectionMap.Add(hostConnectionId, 1);
            return model;
        }
    }
    
    public struct SessionModifyCacheModel : IPendingCache
    {
        public int LifeTime { get; set; }
        public SessionInfoModel SessionInfoModel { get; }

        public SessionModifyCacheModel(int lifeTime, SessionInfoModel sessionInfoModel)
        {
            LifeTime = lifeTime;
            SessionInfoModel = sessionInfoModel;
        }
    }
    
    public struct SessionJoinCacheModel : IPendingCache
    {
        public int LifeTime { get; set; }
        public ulong SessionId { get; }

        public SessionJoinCacheModel(int lifeTime, ulong sessionId)
        {
            LifeTime = lifeTime;
            SessionId = sessionId;
        }
    }
    
    public struct SessionCreationCacheModel : IPendingCache
    {
        public int LifeTime { get; set; }
        public SessionInfoModel SessionInfoModel { get; }

        public SessionCreationCacheModel(int lifeTime, SessionInfoModel sessionInfoModel)
        {
            LifeTime = lifeTime;
            SessionInfoModel = sessionInfoModel;
        }
    }
    
    public interface IPendingCache
    {
        int LifeTime { get; set; }
    }
}