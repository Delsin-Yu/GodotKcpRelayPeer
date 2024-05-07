using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using kcp2k;
using KcpGameServer.Managers.Pending;
using KcpGameServer.Models;

namespace KcpGameServer
{
    /// <summary>
    /// 管理房间创建，加入，以及数据中继的核心类
    /// </summary>
    /// <remarks>
    /// 在下文中，将通过“房间IO”用于指代“房间的创建/加入/修改”
    /// <li>此服务使用Http请求作为房间IO的请求的入口，使用Kcp作为数据中继的核心。</li>
    /// <li>用户可以自由的连接到服务器的Kcp端口，在通过Kcp握手后，这个连接将被定义为“未授权的连接”并在<see cref="PENDING_KCP_CONNECTION_LIFETIME"/>秒后自动断开</li>
    /// <li>只有当用户成功的创建或加入房间后，用户的Kcp连接才会被长久保留</li>
    /// <li>在接受到Http请求并成功后，会为用户请求的房间IO信息生成时效性的“令牌-缓存对”，并且将令牌通过Response返回</li>
    /// <li>在此令牌的时限之内，需要用户通过Kcp连接发送对应的缓存给服务器，以“确认”此动作</li>
    /// <li>任何其余的未经过Http请求就执行的Kcp数据传输操作都会导致服务器断开和客户端的Kcp连接</li>
    /// </remarks>
    public class SessionManager : IDisposable
    {
       
#region Http Pending Buffer

        /// <summary> 
        /// 所有通过Http请求的，等待Kcp验证的房间创建请求，每个请求缓存将会保留<see cref="PENDING_SESSION_CREATION_LIFETIME"/>秒
        /// </summary>
        private readonly TokenPendingBuffer<SessionCreationCacheModel> _pendingSessions;

        /// <summary> 
        /// 所有通过Http请求的，等待Kcp验证的房间加入请求，每个请求缓存将会保留<see cref="PENDING_SESSION_JOIN_LIFETIME"/>秒
        /// </summary>
        private readonly TokenPendingBuffer<SessionJoinCacheModel> _pendingJoins;

        /// <summary> 
        /// 所有通过Http请求的，等待Kcp验证的房间信息修改请求，每个请求缓存将会保留<see cref="PENDING_SESSION_MODIFY_LIFETIME"/>秒
        /// </summary>
        private readonly TokenPendingBuffer<SessionModifyCacheModel> _pendingModify;

#endregion

#region Client - Host - Session Mapping

        /// <summary>
        /// 记录一个用户连接ID所需要转发给的房主连接ID
        /// </summary>
        private readonly ConcurrentDictionary<int, int> _clientToHostMapping = new();

        /// <summary>
        /// 记录一个房主连接ID所对应的房间模型
        /// </summary>
        private readonly ConcurrentDictionary<int, SessionModel> _hostToSessionMapping = new();

        /// <summary>
        /// 记录一个房间ID所对应的房间模型
        /// </summary>
        private readonly ConcurrentDictionary<ulong, SessionModel> _sessions = new();

#endregion

#region Pending Time

        /// <summary>
        /// 通过Http请求房间创建时该请求缓存被保存的时长
        /// </summary>
        private const int PENDING_SESSION_CREATION_LIFETIME = 30;

        /// <summary>
        /// 通过Http请求房间信息修改时该请求缓存被保存的时长
        /// </summary>
        private const int PENDING_SESSION_MODIFY_LIFETIME = 30;

        /// <summary>
        /// 通过Http请求房间加入时该请求缓存被保存的时长
        /// </summary>
        private const int PENDING_SESSION_JOIN_LIFETIME = 30;

        /// <summary>
        /// 通过Http请求房间创建时该请求缓存被保存的时长
        /// </summary>
        private const int PENDING_KCP_CONNECTION_LIFETIME = 30;

#endregion

        /// <summary>
        /// 管理器自身的生命周期TokenSource，在管理器被丢弃时自动Cancel
        /// </summary>
        private readonly CancellationTokenSource _tokenSource = new();

        /// <summary>
        /// 负责数据收发的Kcp服务器
        /// </summary>
        private readonly KcpServer _server;

        /// <summary>
        /// 反序列化一个Guid所需要的数组长度
        /// </summary>
        private const int GUID_BYTE_ARRAY_LENGTH = 16;

        [Conditional("DEBUG")]
        private static void DebugLog(string message, ConsoleColor color = ConsoleColor.Cyan)
        {
            var cached = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"[DEBUG] {message}");
            Console.ForegroundColor = cached;
        }
        
        public SessionManager(IConfiguration configuration)
        {
#region 初始化所有缓存

            Log.Info = static message => DebugLog(message, ConsoleColor.Green);
            Log.Warning = static message => DebugLog(message, ConsoleColor.Yellow);
            Log.Error = static message => DebugLog(message, ConsoleColor.Red);

            KcpPendingBuffer pendingKcpConnections = new(
                connectionId => DisconnectClientWithReason(connectionId, KcpTerminateReason.TimeOut),
                _tokenSource.Token);
            _pendingSessions = new(_tokenSource.Token);
            _pendingJoins = new(_tokenSource.Token);
            _pendingModify = new(_tokenSource.Token);

#endregion
            
#region 初始化服务器

            _server = new(
                // OnConnected
                connectionId =>
                {
                    DebugLog($"Kcp: OnConnected: {connectionId}");
                    
                    // 在一个新的未授权Kcp连接出现的时候，将其加入临时Kcp连接缓存中
                    if (DisconnectIfTrue(
                            !pendingKcpConnections.TryAddPendingBuffer(connectionId, new(PENDING_KCP_CONNECTION_LIFETIME)),
                            connectionId,
                            KcpTerminateReason.ServerSideError))
                    {
                        // 如果Kcp缓存已存在，则断开连接
                        DebugLog("[Fail!] Connection already pending", ConsoleColor.Red);
                    }
                    
                    DebugLog($"[Success!]", ConsoleColor.Green);
                },
                // OnData
                (connectionId, data, channel) =>
                {
                    // 如果Kcp信息来自非可靠通道，则自动断开连接
                    if (DisconnectIfTrue(
                            channel != KcpChannel.Reliable,
                            connectionId,
                            KcpTerminateReason.UnreliableCommunicationNotAllowed))
                    {
                        DebugLog($"[Error!] {connectionId}: Unreliable Communication Not Allowed", ConsoleColor.Red);
                        return;
                    }

                    // 如果信息长度错误，则自动断开连接
                    if (DisconnectIfTrue(
                            data.Length <= 1,
                            connectionId,
                            KcpTerminateReason.InvalidPayloadLength))
                    {
                        return;
                    }

                    var dataSpan = data.Span;
                    
                    // 获得信息的类型
                    var messageType = (KcpClientMessageType)dataSpan[0];

                    // 并且截取之后的信息
                    var dataWithoutHeader = dataSpan[1..];

                    // 将不同的信息类型传递给不同的负责方法
                    switch (messageType)
                    {
                        case KcpClientMessageType.JoinSession:
                            
                            DebugLog($"Kcp: OnData.JoinSession: {connectionId}");
                            
                            var success = HandleJoinTypeMessage(connectionId, dataWithoutHeader);
                            if (success)
                            {
                                var extractResult = pendingKcpConnections.TryExtractBuffer(connectionId, out _);
                                if (extractResult is ExtractResult.Success)
                                {
                                    DebugLog($"[Success!]", ConsoleColor.Green);
                                }
                                else
                                {
                                    DebugLog($"[Error!] {extractResult}", ConsoleColor.Red);
                                }
                            }
                            else
                            {
                                DebugLog($"[Error!] success == false", ConsoleColor.Red);
                            }
                            

                            break;
                        case KcpClientMessageType.AuthSession:
                            
                            DebugLog($"Kcp: OnData.AuthSession: {connectionId}");
                            
                            success = HandleAuthTypeMessage(connectionId, dataWithoutHeader);
                            if (success)
                            {
                                var extractResult = pendingKcpConnections.TryExtractBuffer(connectionId, out _);
                                if (extractResult is ExtractResult.Success)
                                {
                                    DebugLog($"[Success!]", ConsoleColor.Green);
                                }
                                else
                                {
                                    DebugLog($"[Error!] {extractResult}", ConsoleColor.Red);
                                }
                            }
                            else
                            {
                                DebugLog($"[Error!] success == false", ConsoleColor.Red);
                            }
                            
                            break;
                        case KcpClientMessageType.ModifySession:
                            
                            DebugLog($"Kcp: OnData.ModifySession: {connectionId}");
                            
                            // 未授权的Kcp连接禁止使用修改房间数据的功能
                            if (DisconnectIfTrue(
                                    pendingKcpConnections.IsPending(connectionId),
                                    connectionId,
                                    KcpTerminateReason.UnAuthorizedAction))
                            {
                                DebugLog($"[Error!] UnAuthorizedAction", ConsoleColor.Red);
                                return;
                            }

                            HandleModifyTypeMessage(connectionId, dataWithoutHeader);
                                                     
                            DebugLog($"[Success!] ", ConsoleColor.Green);
                            break;
                        case KcpClientMessageType.Payload:
                            
                            // DebugLog($"Kcp: OnData.Payload: {connectionId}");
                            
                            // 未授权的Kcp连接禁止发送有效数据
                            if (DisconnectIfTrue(
                                    pendingKcpConnections.IsPending(connectionId),
                                    connectionId,
                                    KcpTerminateReason.UnAuthorizedAction))
                            {
                                DebugLog($"[Error!] UnAuthorizedAction", ConsoleColor.Red);
                                return;
                            }

                            HandlePayloadMessage(connectionId, dataWithoutHeader);
                            
                            // DebugLog($"[Success!] ", ConsoleColor.Green);
                            break;
                        case KcpClientMessageType.DisconnectClient:
                            
                            DebugLog($"Kcp: OnData.DisconnectClient: {connectionId}");
                            
                            // 未授权的Kcp连接禁止发送断开命令
                            if (DisconnectIfTrue(
                                    pendingKcpConnections.IsPending(connectionId),
                                    connectionId,
                                    KcpTerminateReason.UnAuthorizedAction))
                            {
                                DebugLog($"[Error!] UnAuthorizedAction", ConsoleColor.Red);
                                return;
                            }

                            HandleDisconnectClientTypeMessage(connectionId, dataWithoutHeader);
                            
                            DebugLog($"[Success!]", ConsoleColor.Green);
                            break;
                        default:
                            
                            DebugLog($"Kcp: OnData.Unknown({messageType}): {connectionId}");
                            
                            // 在信息类型未知的情况下，断开连接
                            DisconnectClientWithReason(connectionId, KcpTerminateReason.UnrecognizableMessageHeader);
                           
                            DebugLog($"[Success!]", ConsoleColor.Green);
                            break;
                    }
                },
                // OnDisconnected
                HandleDisconnection,
                // OnError
                (connectionId, errorCode, reason) =>
                {
                    // 服务端收到错误信息时自动断开连接
                    Console.WriteLine($"Kcp: Error on connectionId: {connectionId}, {errorCode}, {reason}");
                    HandleDisconnection(connectionId);
                    DisconnectClientWithReason(connectionId, KcpTerminateReason.ServerSideError);
                },
                new(
                    DualMode: configuration.GetValue<bool>("Kcp_DualMode"),
                    NoDelay: configuration.GetValue<bool>("Kcp_NoDelay"),
                    Interval: configuration.GetValue<uint>("Kcp_Interval"),
                    Timeout: configuration.GetValue<int>("Kcp_Timeout"),
                    RecvBufferSize: configuration.GetValue<int>("Kcp_RecvBufferSize"),
                    SendBufferSize: configuration.GetValue<int>("Kcp_SendBufferSize"),
                    FastResend: configuration.GetValue<int>("Kcp_FastResend"),
                    ReceiveWindowSize: configuration.GetValue<uint>("Kcp_ReceiveWindowSize"),
                    SendWindowSize: configuration.GetValue<uint>("Kcp_SendWindowSize"),
                    MaxRetransmits: configuration.GetValue<uint>("Kcp_MaxRetransmit")
                ));

            void HandleDisconnection(int connectionId)
            {
                // 如果为未授权连接，则从未授权连接中移除
                if (pendingKcpConnections.TryExtractBuffer(connectionId, out _) == ExtractResult.Success)
                {

                    DebugLog($"Kcp: OnDisconnected.pendingKcpConnections: {connectionId}");

                    return;
                }

                // 如果为主机，则从主机中移除，并且断开所有成员的连接，关闭房间
                if (_hostToSessionMapping.TryRemove(connectionId, out var sessionModel))
                {

                    DebugLog($"Kcp: OnDisconnected._hostToSessionMapping: {connectionId}");

                    var removeSucceed = _sessions.TryRemove(sessionModel.SessionId, out _);
                    if (!removeSucceed) Console.WriteLine($"Kcp: Error when trying to remove session");
                    lock (sessionModel)
                    {
                        foreach (var clientConnectionId in sessionModel.ConnectionMap.ConnectionIds)
                        {
                            DisconnectClientWithReason(clientConnectionId, KcpTerminateReason.HostShutdown);
                        }
                    }

                    UidManager.Release(sessionModel.SessionId);
                    return;
                }

                // 如果为成员，则告知主机目标成员已经断开连接
                if (_clientToHostMapping.TryRemove(connectionId, out var hostConnectionId))
                {

                    DebugLog($"Kcp: OnDisconnected._clientToHostMapping: {connectionId}");

                    Span<byte> buffer = stackalloc byte[4];
                    BitConverter.TryWriteBytes(buffer, connectionId);
                    SendPayload(hostConnectionId, KcpServerMessageType.ClientDisconnected, buffer);

                    if (!_hostToSessionMapping.TryGetValue(hostConnectionId, out sessionModel)) return;
                    lock (sessionModel.ConnectionMap)
                    {
                        sessionModel.ConnectionMap.Remove(connectionId);
                    }
                }
            }

            // 启动Kcp服务
            var port = configuration.GetValue<ushort>("KcpPort");
            _server.Start(port);
            Console.WriteLine($"Kcp: Server started at port: {port}");

#endregion

#region Kcp Tick

            // Kcp服务器Tick
            var kcpTick = new Thread(
                () =>
                {
                    // 循环更新Tick以及ArrayPoolGC
                    while (!_tokenSource.IsCancellationRequested)
                    {
                        _server.Tick();
                    }

                    // 服务器关闭的时候，断开所有连接
                    foreach (var connectionId in _server.connections.Keys.ToArray())
                    {
                        DisconnectClientWithReason(connectionId, KcpTerminateReason.ServerShutdown);
                    }

                    // 停止服务器
                    _server.Stop();
                });
            kcpTick.Start();

#endregion
        }

#region Message Handling

        private void HandleDisconnectClientTypeMessage(int connectionId, ReadOnlySpan<byte> payload)
        {
            // 断开用户连接的数据必须等于4Byte（目标ID）
            if (DisconnectIfTrue(
                    payload.Length != 4,
                    connectionId,
                    KcpTerminateReason.InvalidDisconnectClientPayloadLength))
            {
                return;
            }

            // 如果当前ID并不是房主，则直接断开
            if (DisconnectIfTrue(
                    !_hostToSessionMapping.TryGetValue(connectionId, out var clientSet),
                    connectionId,
                    KcpTerminateReason.UnAuthorizedAction))
            {
                return;
            }

            // 获得目标ID
            var requestedConnectionId = BitConverter.ToInt32(payload[..4]);

            // 如果当前房间中不存在房主需要断开的连接ID，则返回
            if (!clientSet!.ConnectionMap.HasConnectionId(requestedConnectionId))
            {
                return;
            }

            // 断开目标用户的连接
            DisconnectClientWithReason(requestedConnectionId, KcpTerminateReason.HostTriggeredDisconnection);
        }


        /// <summary>
        /// 处理有效类型的信息
        /// </summary>
        /// <param name="senderConnectionId"></param>
        /// <param name="payload"></param>
        private void HandlePayloadMessage(int senderConnectionId, ReadOnlySpan<byte> payload)
        {
            // 有效数据不可能小于等于4Byte（目标ID+有效数据）
            if (DisconnectIfTrue(
                    payload.Length <= 4,
                    senderConnectionId,
                    KcpTerminateReason.InvalidPayloadLength))
            {
                return;
            }
            
            // Payload 结构如下：
            // [4] Byte 接收者本地ID
            // [4] Byte 通道ID
            // [1] Byte 传送类型枚举
            // [N] Byte Godot负载
            
            // 在这里我们需要
            // 读取Payload开头4个Byte的接收者本地ID
            // 计算出这个数据包的目标接收者的Kcp连接ID
            // 然后，在用户发送给房主的情况下，将Payload开头4个Byte的接收者本地ID替换成发送者的连接ID
            // 或者，在房主发送给用户的情况下，将Payload开头4个Byte的接收者本地ID替换成1
            
            // 获得接收者本地ID
            var sendToLocalId = BitConverter.ToInt32(payload[..4]);

            int sendToConnectionId;

            // 如果发送的ID是1，就代表这是用户在发送数据给房主
            if (sendToLocalId == 1)
            {
                // 如果当前的连接并没有被建立映射的话直接断开
                if (DisconnectIfTrue(
                        !_clientToHostMapping.TryGetValue(senderConnectionId, out sendToConnectionId),
                        senderConnectionId,
                        KcpTerminateReason.UnAuthorizedAction)) return;
                
                // 根据房主ID查出所属房间信息
                if (DisconnectIfTrue(
                        !_hostToSessionMapping.TryGetValue(sendToConnectionId, out var sessionInfo),
                        senderConnectionId,
                        KcpTerminateReason.UnAuthorizedAction)) return;
            }
            // 否则这就是房主在发送数据给用户
            else
            {
                // 如果当前ID并不是房主，则直接断开（用户不可能发给ID不是1的终端）
                if (DisconnectIfTrue(
                        !_hostToSessionMapping.TryGetValue(senderConnectionId, out var sessionInfo),
                        senderConnectionId,
                        KcpTerminateReason.UnAuthorizedAction)) return;

                // 如果当前房间中不存在房主需要发送给的连接ID，则不要转发（目标已经断开连接）
                if (!sessionInfo!.ConnectionMap.TryGetConnectionId(sendToLocalId, out sendToConnectionId)) return;

                senderConnectionId = 1;
            }

            Span<byte> redirectedPayload = stackalloc byte[payload.Length];
            payload.CopyTo(redirectedPayload);
            BitConverter.TryWriteBytes(redirectedPayload, senderConnectionId);
            
            SendPayload(sendToConnectionId, KcpServerMessageType.PayloadRelay, redirectedPayload);
        }

        /// <summary>
        /// 处理加入房间类型的信息
        /// </summary>
        /// <param name="connectionId"></param>
        /// <param name="payload"></param>
        private bool HandleJoinTypeMessage(int connectionId, ReadOnlySpan<byte> payload)
        {
            // 如果信息中没有Guid就断开
            if (DisconnectIfNoGuidFromPayload(
                    connectionId,
                    payload,
                    out var guid)) return false;

            // 如果当前Token不存在的话就断开
            if (DisconnectIfRemoveKeyFailed(
                    _pendingJoins,
                    guid,
                    connectionId,
                    out var cacheModel)) return false;

            // 如果目标ID的房间不存在的话就断开
            if (DisconnectIfTrue(
                    !_sessions.TryGetValue(cacheModel.SessionId, out var sessionModel),
                    connectionId,
                    KcpTerminateReason.InvalidSessionId)) return false;

            // 如果房间满了就断开
            if (DisconnectIfTrue(
                    sessionModel!.IsSessionFull(out var currentCount),
                    connectionId,
                    KcpTerminateReason.SessionFull)) return false;

            var hostConnectionId = sessionModel.HostConnectionId;

            // 如果当前连接已经存在于查表中的话就断开
            if (DisconnectIfTrue(
                    !_clientToHostMapping.TryAdd(connectionId, hostConnectionId),
                    connectionId,
                    KcpTerminateReason.ServerSideError))
            {
                Console.WriteLine("!failed on join session: client to host mapping already exists!");
                return false;
            }

            var userLocalId = currentCount + 1;

            // 将当前用户加入到房间的用户连接表中
            lock (sessionModel.ConnectionMap)
            {
                sessionModel.ConnectionMap.Add(connectionId, userLocalId);
            }

            // 告诉连接方成功完成

            Span<byte> localIdSpan = [0, 0, 0, 0];
            BitConverter.TryWriteBytes(localIdSpan, userLocalId);

            SendPayload(connectionId, KcpServerMessageType.Success, localIdSpan);

            Span<byte> hostMessageSpan = [0, 0, 0, 0, 0, 0, 0, 0];
            
            BitConverter.TryWriteBytes(hostMessageSpan, connectionId);
            
            localIdSpan.CopyTo(hostMessageSpan[4..]);

            // 告诉房主新的成员加入房间
            SendPayload(hostConnectionId, KcpServerMessageType.ClientConnected, hostMessageSpan);

            return true;
        }

        /// <summary>
        /// 处理房间修改类型的信息
        /// </summary>
        /// <param name="connectionId"></param>
        /// <param name="payload"></param>
        private void HandleModifyTypeMessage(int connectionId, ReadOnlySpan<byte> payload)
        {
            // 如果信息中没有Guid就断开
            if (DisconnectIfNoGuidFromPayload(
                    connectionId,
                    payload,
                    out var guid)) return;

            // 如果当前Token不存在的话就断开
            if (DisconnectIfRemoveKeyFailed(
                    _pendingModify,
                    guid,
                    connectionId,
                    out var cacheModel)) return;

            // 如果当前连接ID并不属于任何房间就断开
            if (DisconnectIfTrue(
                    !_hostToSessionMapping.TryGetValue(connectionId, out var sessionModel),
                    connectionId,
                    KcpTerminateReason.InvalidSessionId)) return;

            // 修改房间信息
            sessionModel!.ModifySessionInfo(cacheModel.SessionInfoModel);

            // 告诉连接方成功完成
            SendPayload(connectionId, KcpServerMessageType.Success, []);
        }

        /// <summary>
        /// 处理房间建立类型的信息
        /// </summary>
        /// <param name="connectionId"></param>
        /// <param name="payload"></param>
        private bool HandleAuthTypeMessage(int connectionId, ReadOnlySpan<byte> payload)
        {
            // 如果信息中没有Guid就断开
            if (DisconnectIfNoGuidFromPayload(
                    connectionId,
                    payload,
                    out var guid)) return false;

            // 如果当前Token不存在的话就断开
            if (DisconnectIfRemoveKeyFailed(
                    _pendingSessions,
                    guid,
                    connectionId,
                    out var cacheModel)) return false;

            var sessionUid = UidManager.Get();

            // 如果无法再分配房间ID就断开
            if (DisconnectIfTrue(
                    sessionUid == null,
                    connectionId,
                    KcpTerminateReason.ServerSideError)) return false;

            var uid = sessionUid!.Value;
            var model = SessionModel.FromInfoModel(uid, connectionId, cacheModel.SessionInfoModel);

            // 如果相同的房间ID已经被注册就断开
            if (DisconnectIfTrue(
                    !_sessions.TryAdd(uid, model),
                    connectionId,
                    KcpTerminateReason.ServerSideError))
            {
                Console.WriteLine("!Failed on adding sessions: duplicate session id!");
                return false;
            }

            // 如果房主-房间已被注册就断开
            if (DisconnectIfTrue(
                    !_hostToSessionMapping.TryAdd(connectionId, model),
                    connectionId,
                    KcpTerminateReason.ServerSideError))
            {
                _ = _sessions.TryRemove(uid, out _);
                Console.WriteLine("!failed on join session: host to session mapping already exists!");
                UidManager.Release(uid);
                return false;
            }

            Span<byte> buffer = [0, 0, 0, 0];
            BitConverter.TryWriteBytes(buffer, 1);
            
            // 告诉连接方成功完成
            SendPayload(connectionId, KcpServerMessageType.Success, buffer);
            return true;
        }

#endregion

#region Utils

        private bool DisconnectIfNoGuidFromPayload(int connectionId, ReadOnlySpan<byte> payload, out Guid guid)
        {
            if (DisconnectIfTrue(payload.Length != GUID_BYTE_ARRAY_LENGTH, connectionId, KcpTerminateReason.InvalidTokenPayloadLength))
            {
                guid = Guid.Empty;
                return true;
            }

            guid = new(payload);
            return false;
        }

        private bool DisconnectIfRemoveKeyFailed<T>
            (
                TokenPendingBuffer<T> buffer,
                in Guid guid,
                int connectionId,
                out T value,
                [CallerArgumentExpression(nameof(buffer))]
                string dictionaryName = ""
            ) where T : struct, IPendingCache
        {
            switch (buffer.TryExtractBuffer(guid, out value))
            {
                case ExtractResult.InvalidKey:

                    DebugLog($"Kcp: Invalid Auth Token: {guid.ToString()}", ConsoleColor.Red);

                    DisconnectClientWithReason(connectionId, KcpTerminateReason.InvalidAuthToken);
                    return true;
                case ExtractResult.InternalError:
                    Console.WriteLine($"Kcp: !Failed to remove guid from {dictionaryName}!");
                    DisconnectClientWithReason(connectionId, KcpTerminateReason.ServerSideError);
                    return true;
                case ExtractResult.Success:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ExtractResult), "未支持的类型！");
            }
        }

        private bool DisconnectIfTrue(bool condition, int connectionId, KcpTerminateReason reason, [CallerArgumentExpression(nameof(condition))] string conditionArg = "")
        {
            if (!condition) return false;
            DebugLog($"Kcp: DisconnectIfTrue: {conditionArg}", ConsoleColor.Red);
            DisconnectClientWithReason(connectionId, reason);
            return true;
        }

        private void DisconnectClientWithReason(int connectionId, KcpTerminateReason reason)
        {

            DebugLog($"Kcp: DisconnectClientWithReason: {connectionId}:{reason}", ConsoleColor.Red);

            Span<byte> encodedTerminateReasonArray = [(byte)reason];
            SendPayload(connectionId, KcpServerMessageType.ServerSideDisconnection, encodedTerminateReasonArray);
            _server.Disconnect(connectionId);
        }

        private void SendPayload(int connectionId, KcpServerMessageType messageType, ReadOnlySpan<byte> payload)
        {
            var payloadCount = payload.Length + 1;
            Span<byte> encodedPayloadArray = stackalloc byte[payloadCount];
            encodedPayloadArray[0] = (byte)messageType;
            payload.CopyTo(encodedPayloadArray[1..]);
            _server.Send(connectionId, encodedPayloadArray, KcpChannel.Reliable);

            // DebugLog($"Kcp: Payload Sent to {connectionId}: {messageType}, length({payload.Length})");
        }
        

#endregion

#region Http Api

        public SessionPreviewModelArray ListSessions()
        {

            DebugLog($"Http: ListSessions");

            return new(_sessions.Values.Select(SessionPreviewModel.FromSession).ToArray());
        }

        public TokenModel AllocateSession(SessionInfoModel sessionInfoModel)
        {
            var token = _pendingSessions.AddPendingBuffer(new(PENDING_SESSION_CREATION_LIFETIME, sessionInfoModel));

            DebugLog($"Http: AllocateSession: {sessionInfoModel.SessionName}({sessionInfoModel.MaxMemberCount}), {token}");

            return TokenModel.FromToken(token);
        }

        public TokenModel JoinSession(ulong sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var sessionModel)) return TokenModel.FromError("Invalid Session Id");
            if (sessionModel.IsSessionFull(out _)) return TokenModel.FromError("Session Full");

            var token = _pendingJoins.AddPendingBuffer(new(PENDING_SESSION_JOIN_LIFETIME, sessionId));

            DebugLog($"Http: JoinSession[{sessionId}]: {token}");

            return TokenModel.FromToken(token);
        }

        public TokenModel ModifySession(in SessionInfoModel sessionInfoModel)
        {
            if (!sessionInfoModel.IsValid()) return TokenModel.FromError("Invalid Session Id");

            var token = _pendingModify.AddPendingBuffer(new(PENDING_SESSION_MODIFY_LIFETIME, sessionInfoModel));

            DebugLog($"Http: ModifySession: {sessionInfoModel.SessionName}({sessionInfoModel.MaxMemberCount}), {token}");

            return TokenModel.FromToken(token);
        }

#endregion

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _tokenSource.Dispose();
        }
    }
}
