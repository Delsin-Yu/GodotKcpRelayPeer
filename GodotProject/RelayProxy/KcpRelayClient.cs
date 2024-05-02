using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;
using GodotTask;
using kcp2k;
using KcpLog = kcp2k.Log;

namespace GodotMultiplayerExperiment.RelayProxyV2;

internal sealed class KcpRelayClient : IDisposable
{
    private readonly string _serverAddress;
    private readonly ushort _port;
    private readonly RelayRole _relayRole;
    private readonly IKcpNotificationListener _listener;
    
    private CancellationTokenSource _pendingRequestToken;
    
    private readonly KcpClient _kcpClient; 
    private bool _isStarted;
    private KcpTerminateReason? _disconnectReason;

    private readonly HashSet<int> _currentConnectedClient = [];
    
    public int ID { get; private set; }
    
    public KcpRelayClient(string serverAddress, ushort port, RelayRole relayRole, KcpConfig kcpConfig, IKcpNotificationListener listener)
    {
        _serverAddress = serverAddress;
        _port = port;
        _relayRole = relayRole;
        _listener = listener;
        _kcpClient = new(static () => { }, OnData, OnDisconnected, OnError, kcpConfig);
    }

    public RelayRole Role => _relayRole;
    public bool BlockFurtherConnection { get; set; }

    public async GDTask<Response> InitKcpConnection()
    {
        if (_isStarted) throw new InvalidOperationException("禁止在已开启Kcp服务的情况下再次开启");
        
        _isStarted = true;
        ToggleManualLoop(true);
        _hasRegistered = true;
        _kcpClient.Connect(_serverAddress, _port);
        
        await GDTask.WaitUntil(() => _kcpClient.connected || !_isStarted);
        
        if (!_isStarted)
        {
            KcpLog.Error("[Kcp Relay Client] Error when creating kcp connection to server");
            ToggleManualLoop(false);
            return Response.FromFail("Error when creating kcp connection to server", null);
        }

        KcpLog.Info("[Kcp Relay Client] Kcp Connection initialization successful");

        return Response.FromSuccess();
    }

    
    internal async GDTask<Response> SendKcpAuthConnectionAsync(AuthType authType, ReadOnlyMemory<byte> binaryGuid, CancellationToken token)
    {
        if (IsDisconnected(out var response))
        {
            ToggleManualLoop(false);
            return response.Value;
        }

        _isStarted = true;

        SendPayload(authType == AuthType.CreateSession ? KcpClientMessageType.AuthSession : KcpClientMessageType.JoinSession, binaryGuid.Span);

        _pendingRequestToken = new();
        
        await GDTask.WaitUntil(() => token.IsCancellationRequested || _pendingRequestToken.IsCancellationRequested, cancellationToken: CancellationToken.None);

        ToggleManualLoop(false);

        _pendingRequestToken.Dispose();
        _pendingRequestToken = null;

        if (IsDisconnected(out response))
        {
            return response.Value;
        }

        if (token.IsCancellationRequested)
        {
  
            KcpLog.Error("Fail! [Kcp Relay Client] Kcp authentication timeout");

            return Response.FromFail("Kcp Authentication Timeout", null);
        }

        KcpLog.Info("[Kcp Relay Client] Kcp authentication successful");
        
        return Response.FromSuccess();
    }
    
    public void AbortConnection()
    {
        KcpLog.Info("[Kcp Relay Client] Local Abort Connection");
        _kcpClient.Disconnect();
    }

    public void SendGodotPayload(int clientId, ReadOnlySpan<byte> payload)
    {
        Span<byte> data = stackalloc byte[payload.Length + 4];
        BitConverter.TryWriteBytes(data, clientId);
        payload.CopyTo(data[4..]);
        SendPayload(KcpClientMessageType.GodotPayload, data);
    }
    
    public void SendDisconnectClientPayload(int clientId)
    {
        Span<byte> data = [0, 0, 0, 0];
        BitConverter.TryWriteBytes(data, clientId);
        SendPayload(KcpClientMessageType.DisconnectClient, data);
    }

    private bool IsDisconnected([NotNullWhen(true)] out Response? response)
    {
        if (!_isStarted)
        {
            KcpLog.Error("Fail! [Kcp Relay Client] Client has disconnected");
            
            response = Response.FromFail($"Connection Aborted: {_disconnectReason?.ToString() ?? "Unknown"}", null);
            return true;
        }
        
        response = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SendPayload(KcpClientMessageType messageType, ReadOnlySpan<byte> payload)
    {
        Span<byte> fullMessageSpan = stackalloc byte[payload.Length + 1];
        fullMessageSpan[0] = (byte)messageType;
        payload.CopyTo(fullMessageSpan[1..]);
        _kcpClient.Send(fullMessageSpan, KcpChannel.Reliable);
        KcpLog.Info($"[color=green]Send: {Print(payload)}");
    }

    private static string Print(ReadOnlySpan<byte> payload) =>
        $"{payload.Length} bytes: \n{
            string
                .Join(
                    '\n',
                    payload
                        .ToArray()
                        .Chunk(16)
                        .Select(
                            x => string
                                .Join(
                                    ' ',
                                    x
                                        .Select(
                                            y => y
                                                .ToString("D3")
                                        )
                                )
                        )
                )}";

    private void OnError(ErrorCode errorCode, string reason)
    {
        KcpLog.Error($"[Kcp Relay Client] Error: {errorCode}, {reason ?? "No Reason"}");
        _kcpClient.Disconnect();
    }

    private void OnDisconnected()
    {
        KcpLog.Info($"[Kcp Relay Client] OnDisconnected\n{new StackTrace()}");
        _isStarted = false;
        if (_relayRole == RelayRole.Client)
        {
            _listener.EmitPeerDisconnected(1);
        }

        _listener.Cleanup();
    }

    private void OnData(ReadOnlyMemory<byte> memory, KcpChannel channel)
    {
        // 如果Kcp信息来自非可靠通道，则返回
        if (channel == KcpChannel.Unreliable)
        {
            KcpLog.Warning("[Kcp Relay Client] Kcp Data: KcpChannel.Unreliable");
            return;
        }

        var data = memory.Span;
        
        // 获得信息的类型
        var messageType = (KcpServerMessageType)data[0];

        // 并且截取之后的信息
        var payload = data[1..];

        switch (messageType)
        {
            case KcpServerMessageType.ServerSideDisconnection:
                
                _disconnectReason = (KcpTerminateReason)payload[0];
                _kcpClient.Disconnect();
                
                return;
            case KcpServerMessageType.ClientConnected:

                if (_relayRole == RelayRole.Client)
                {
                    KcpLog.Warning("[Kcp Relay Client] Server is notifying client that a client is connected, this is redundant and useless!");
                    return;
                }

                if (payload.Length != 4)
                {
                    KcpLog.Error($"[Kcp Relay Client] Client Connected/Disconnected requires payload length of 4 instead of {payload.Length}!");
                    return;
                }

                var clientId = BitConverter.ToInt32(payload);

                if (BlockFurtherConnection || clientId < 2 || !_currentConnectedClient.Add(clientId))
                {
                    SendDisconnectClientPayload(clientId);
                    return;
                }
                
                _listener.EmitPeerConnected(clientId);
                
                break;
            case KcpServerMessageType.ClientDisconnected:

                if (_relayRole == RelayRole.Client)
                {
                    KcpLog.Warning("[Kcp Relay Client] Server is notifying client that a client is disconnected, this is redundant and useless!");
                    return;
                }

                if (payload.Length != 4)
                {
                    KcpLog.Error($"[Kcp Relay Client] Client Connected/Disconnected requires payload length of 4 instead of {payload.Length}!");
                    return;
                }
                
                clientId = BitConverter.ToInt32(payload);

                if (!_currentConnectedClient.Remove(clientId))
                {
                    return;
                }
                
                _listener.EmitPeerDisconnected(clientId);
                
                break;
            case KcpServerMessageType.GodotPayloadRelay:

                // Godot的数据不可能小于等于4Byte（目标ID+Godot数据）
                if (payload.Length <= 4)
                {
                    KcpLog.Error($"[Kcp Relay Client] PayloadRelay requires payload length more than 4 instead of {payload.Length}!");
                    return;
                }

                if (_relayRole == RelayRole.Client && !_currentConnectedClient.Add(1))
                {
                    _listener.EmitPeerConnected(1);
                }
                
                _listener.NotifyPayload(payload);
                KcpLog.Info($"[color=yellow]Recv: {Print(payload)}");

                break;
            case KcpServerMessageType.Success:

                if (payload.Length is not 4 and not 0)
                {
                    KcpLog.Error($"[Kcp Relay Client] Client Success requires payload length of 4 or 0 instead of {payload.Length}!");
                    return;
                }
                
                if (_pendingRequestToken is null) return;
                _pendingRequestToken.Cancel();

                if (payload.Length != 0)
                {
                    ID = BitConverter.ToInt32(payload[..4]);
                }
                
                break;
            default:
                KcpLog.Error("[Kcp Relay Client] Unable to parse the message header");
                _kcpClient.Disconnect();
                
                break;
        }
    }

    private bool _hasRegistered = false;
    private void ToggleManualLoop(bool enable)
    {
        if (!enable)
        {
            if (_hasRegistered)
            {
                ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= Tick;
                _hasRegistered = false;
            }
            else
            {
                throw new InvalidOperationException("Not yet registered!");
            }
        }
        else
        {
            if (!_hasRegistered)
            {
                ((SceneTree)Engine.GetMainLoop()).ProcessFrame += Tick;
                _hasRegistered = true;
            }
            else
            {
                throw new InvalidOperationException("Already registered!");
            }
        }
    }
    
    public void Tick() => _kcpClient.Tick();
    public void Dispose()
    {
        _pendingRequestToken?.Dispose();
    }
}