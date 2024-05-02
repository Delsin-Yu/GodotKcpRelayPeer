using System;
using MemoryPack;

// ReSharper disable MemberCanBePrivate.Global

namespace KcpGameServer.Models
{
    /// <summary>
    /// 用于容纳令牌信息的模型
    /// </summary>
    [MemoryPackable]
    public partial struct TokenModel
    {
        public Guid Token { get; }
        public bool HasToken { get; }
        public string? ErrorMessage { get; }

        public TokenModel(Guid token, bool hasToken, string? errorMessage)
        {
            Token = token;
            HasToken = hasToken;
            ErrorMessage = errorMessage;
        }

        public static TokenModel FromToken(in Guid token) => new(token, true, null);
        public static TokenModel FromError(string errorMessage) => new(Guid.Empty, false, errorMessage);
    }
    
    
    /// <summary>
    /// 用于容纳所有房间预览信息的模型
    /// </summary>
    [MemoryPackable]
    public partial struct SessionPreviewModelArray
    {
        public SessionPreviewModel[] Array { get; }

        public SessionPreviewModelArray(SessionPreviewModel[] array) => Array = array;
    }
    
    /// <summary>
    /// 用于展示一个房间预览信息的模型
    /// </summary>
    [MemoryPackable]
    public partial struct SessionPreviewModel
    {
        public ulong SessionId { get; }

        public string SessionName { get; }

        public int MaxMemberCount { get; }

        public int CurrentMemberCount { get; }

        public SessionPreviewModel(ulong sessionId, string sessionName, int maxMemberCount, int currentMemberCount)
        {
            SessionId = sessionId;
            SessionName = sessionName;
            MaxMemberCount = maxMemberCount;
            CurrentMemberCount = currentMemberCount;
        }

#if !UNITY_64 && !UNITY_SWITCH
        public static SessionPreviewModel FromSession(SessionModel sessionModel) => new(sessionModel.SessionId, sessionModel.SessionName, sessionModel.MaxMemberCount, sessionModel.ConnectionMap.Count);
#endif
    }

    /// <summary>
    /// 通过Http请求修改房间信息时所使用的模型
    /// </summary>
    [MemoryPackable]
    public readonly partial struct SessionInfoModel
    {
        public string SessionName { get; }

        public int MaxMemberCount { get; }

        public SessionInfoModel(string sessionName, int maxMemberCount)
        {
            SessionName = sessionName;
            MaxMemberCount = maxMemberCount;
        }

        public bool IsValid() => !string.IsNullOrWhiteSpace(SessionName) && MaxMemberCount > 0;

        public void Deconstruct(out string sessionName, out int maxMemberCount)
        {
            sessionName = SessionName;
            maxMemberCount = MaxMemberCount;
        }
    }

    /// <summary>
    /// Kcp用户发送的信息类型
    /// </summary>
    public enum KcpClientMessageType : byte
    {
        /// <summary>
        /// 授权房间创建：1 Byte <see cref="KcpClientMessageType"/>，16 Byte <see cref="Guid"/>
        /// </summary>
        AuthSession,

        /// <summary>
        /// 授权房间加入：1 Byte <see cref="KcpClientMessageType"/>，16 Byte <see cref="Guid"/>
        /// </summary>
        JoinSession,

        /// <summary>
        /// 授权房间编辑：1 Byte <see cref="KcpClientMessageType"/>，16 Byte <see cref="Guid"/>
        /// </summary>
        ModifySession,

        /// <summary>
        /// 有效数据：1 Byte <see cref="KcpClientMessageType"/>，N Byte Unity数据
        /// </summary>
        Payload,
        
        /// <summary>
        /// 断开用户连接：1 Byte <see cref="KcpClientMessageType"/>，4 Byte <see cref="int"/>>
        /// </summary>
        DisconnectClient
    }

    /// <summary>
    /// Kcp服务端发送回给客户的信息类型
    /// </summary>
    public enum KcpServerMessageType : byte
    {
        /// <summary>
        /// 服务端结束连接：1 Byte <see cref="KcpServerMessageType"/>，1 Byte <see cref="KcpTerminateReason"/>>
        /// </summary>
        ServerSideDisconnection,

        /// <summary>
        /// 一位成员结束连接：1 Byte <see cref="KcpServerMessageType"/>，4 Byte <see cref="int"/>
        /// </summary>
        ClientDisconnected,

        /// <summary>
        /// 一位成员连接：1 Byte <see cref="KcpServerMessageType"/>，4 Byte <see cref="int"/>
        /// </summary>
        ClientConnected,

        /// <summary>
        /// 有效数据：1 Byte <see cref="KcpServerMessageType"/>，N Byte 有效数据
        /// </summary>
        PayloadRelay,

        /// <summary>
        /// 表示操作成功完成：1 Byte <see cref="KcpServerMessageType"/>
        /// </summary>
        Success
    }

    public enum KcpTerminateReason : byte
    {
        /// <summary>
        /// 使用了不安全的连接通道
        /// </summary>
        UnreliableCommunicationNotAllowed,

        /// <summary>
        /// 发送的数据长度错误
        /// </summary>
        InvalidPayloadLength,

        /// <summary>
        /// 服务器无法理解数据的信息头
        /// </summary>
        UnrecognizableMessageHeader,

        /// <summary>
        /// 提供的信息无法被正确转换为Token
        /// </summary>
        InvalidTokenPayloadLength,

        /// <summary>
        /// 提供的Token无效
        /// </summary>
        InvalidAuthToken,

        /// <summary>
        /// 提供了无效的房间ID
        /// </summary>
        InvalidSessionId,

        /// <summary>
        /// 房间已满
        /// </summary>
        SessionFull,

        /// <summary>
        /// 提供了无效的有效数据段长度
        /// </summary>
        InvalidRelayPayloadLength,

        /// <summary>
        /// 提供了无效的断联用户指令数据段长度
        /// </summary>
        InvalidDisconnectClientPayloadLength,

        /// <summary>
        /// 非法操作，被服务器终止服务
        /// </summary>
        UnAuthorizedAction,

        /// <summary>
        /// 房主退出
        /// </summary>
        HostShutdown,

        /// <summary>
        /// 房主断开用户连接
        /// </summary>
        HostTriggeredDisconnection,

        /// <summary>
        /// Kcp以未授权的状态连接时间过长
        /// </summary>
        TimeOut,

        /// <summary>
        /// 服务器端出现问题，已断开连接
        /// </summary>
        ServerSideError,

        /// <summary>
        /// 服务器关闭，正在断开所有连接
        /// </summary>
        ServerShutdown
    }
}
