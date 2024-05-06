using System;
using MemoryPack;

namespace GodotMultiplayerExperiment.RelayProxyV2;

/// <summary>
/// Represents a relay transport event
/// </summary>
public enum NetworkEvent
{
    /// <summary>
    /// New data is received
    /// </summary>
    Payload,

    /// <summary>
    /// A client is connected, or client connected to server
    /// </summary>
    Connect,

    /// <summary>
    /// A client disconnected, or client disconnected from server
    /// </summary>
    Disconnect,

    /// <summary>
    /// Transport has encountered an unrecoverable failure
    /// </summary>
    TransportFailure,
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
    /// Godot数据：1 Byte <see cref="KcpServerMessageType"/>，N Byte Godot数据
    /// </summary>
    GodotPayloadRelay,

    /// <summary>
    /// 表示操作成功完成：1 Byte <see cref="KcpServerMessageType"/>
    /// </summary>
    Success
}

public enum RelayRole
{
    Host, Client
}

internal enum AuthType
{
    CreateSession,
    JoinSession
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
    /// 提供了无效的Godot数据段长度
    /// </summary>
    InvalidGodotPayloadLength,

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
    /// Godot数据：1 Byte <see cref="KcpClientMessageType"/>，N Byte Godot数据
    /// </summary>
    GodotPayload,
        
    /// <summary>
    /// 断开用户连接：1 Byte <see cref="KcpClientMessageType"/>，4 Byte <see cref="int"/>>
    /// </summary>
    DisconnectClient
}

[MemoryPackable]
public partial struct TokenModel
{
    public Guid Token { get; }
    public bool HasToken { get; }
    public string ErrorMessage { get; }

    public TokenModel(Guid token, bool hasToken, string errorMessage)
    {
        Token = token;
        HasToken = hasToken;
        ErrorMessage = errorMessage;
    }

    public static TokenModel FromToken(in Guid token) => new(token, true, null);
    public static TokenModel FromError(string errorMessage) => new(Guid.Empty, false, errorMessage);
}


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

[MemoryPackable]
public partial struct SessionPreviewModelArray
{
    public SessionPreviewModel[] Array { get; }

    public SessionPreviewModelArray(SessionPreviewModel[] array) => Array = array;
}

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

    public readonly override string ToString() => $"ID: {SessionId}/Max: {MaxMemberCount}/Cur: {CurrentMemberCount}/Name: {SessionName}";
}

public readonly struct Response
{
    private Response(bool success, string message, Exception exception)
    {
        Success = success;
        Message = message;
        Exception = exception;
    }

    public bool Success { get; }

    public string Message { get; }

    public Exception Exception { get; }

    public static Response FromSuccess() => new(true, null, null);
    public static Response FromFail(string message, Exception exception) => new(false, message, exception);

    public override string ToString()
    {
        if (Success)
        {
            return "Response: Success";
        }

        return $"""
                Response: Error
                  Message: {Message}
                  Exception: {Exception}
                """;
    }
}

public readonly struct ResultContainer<TValue, TError>
{
    private TValue Value { get; }
    private TError Error { get; }
        
    public bool IsSuccess { get; }
    public bool IsError { get; }

    public bool TryGetValue(out TValue value, out TError error)
    {
        value = Value;
        error = Error;
        return IsSuccess;
    }

    private ResultContainer(TValue value)
    {
        Value = value;
        IsSuccess = true;
        Error = default;
        IsError = false;
    }

    private ResultContainer(TError error)
    {
        Value = default;
        IsSuccess = false;
        IsError = true;
        Error = error;
    }

    public static ResultContainer<TValue, TError> FromSuccess(TValue value) => new(value);
        
    public static ResultContainer<TValue, TError> FromError(TError error) => new(error);
}