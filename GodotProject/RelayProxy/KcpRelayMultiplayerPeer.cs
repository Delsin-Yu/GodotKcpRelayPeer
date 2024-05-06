using System;
using System.Collections.Generic;
using Godot;

namespace GodotMultiplayerExperiment.RelayProxyV2;

public partial class KcpRelayMultiplayerPeer : MultiplayerPeerExtension
{
    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;
    
    /// <remarks>
    /// [4] bytes clientId<br/>
    /// [4] bytes transferChannel<br/>
    /// [1] byte mode<br/>
    /// [Rest] bytes payload<br/>
    /// </remarks>
    private readonly record struct Packet(int ClientId, int TransferChannel, TransferModeEnum Mode, byte[] Payload);
    
    private readonly Variant[] _methodBuffer = new Variant[1];

    private readonly Queue<Packet> _packetQueue = [];
    
    void IKcpNotificationListener.NotifyPayload(in ReadOnlySpan<byte> payload) => 
        _packetQueue.Enqueue(DeserializePacket(payload));

    void IKcpNotificationListener.EmitPeerConnected(int clientId)
    {
        _methodBuffer[0] = clientId;
        EmitSignal(SignalName.PeerConnected, _methodBuffer);
    }

    void IKcpNotificationListener.EmitPeerDisconnected(int clientId)
    {
        _methodBuffer[0] = clientId;
        EmitSignal(SignalName.PeerDisconnected, _methodBuffer);
    }
    
    private static Packet DeserializePacket(ReadOnlySpan<byte> span)
    {
        var clientId = BitConverter.ToInt32(span[..4]);
        var transferChannel = BitConverter.ToInt32(span[4..8]);
        var mode = (TransferModeEnum)span[8];
        var payload = span[9..].ToArray();
        return new(clientId, transferChannel, mode, payload);
    }

    private static int GetPacketSize(ref readonly Packet packet)
    {
        return 4 + 4 + 1 + packet.Payload.Length;
    }
    
    private static void SerializePacket(ref readonly Packet packet, Span<byte> buffer)
    {
        BitConverter.TryWriteBytes(buffer[..4], packet.ClientId);
        BitConverter.TryWriteBytes(buffer.Slice(4, 4), packet.TransferChannel);
        buffer[8] = (byte)packet.Mode;
        packet.Payload.CopyTo(buffer[9..]);
    }

    void IKcpNotificationListener.Cleanup()
    {
        _packetQueue.Clear();
        _connectionStatus = ConnectionStatus.Disconnected;
        _client = null;
    }

    public override void _Close() => _client.AbortConnection();
    public override void _DisconnectPeer(int pPeer, bool pForce) => _client.SendDisconnectClientPayload(pPeer);


    public override ConnectionStatus _GetConnectionStatus() => _connectionStatus;

    public override int _GetMaxPacketSize() => _recvBufferSize;
    public override int _GetUniqueId() => _client.ID;

    public override bool _IsRefusingNewConnections() => _client.BlockFurtherConnection;
    public override void _SetRefuseNewConnections(bool pEnable) => _client.BlockFurtherConnection = pEnable;

    public override bool _IsServer() => _client.Role is RelayRole.Host;

    public override bool _IsServerRelaySupported() => true;

    public override void _Poll() => _client.Tick();

    private int _sendTarget;
    private int _transferChannel;
    private TransferModeEnum _transferMode;
    
    public override Error _PutPacketScript(byte[] pBuffer)
    {
        var packet = new Packet(_sendTarget, _transferChannel, _transferMode, pBuffer);
        Span<byte> serializationBuffer = stackalloc byte[GetPacketSize(in packet)];
        SerializePacket(in packet, serializationBuffer);
        _client.SendGodotPayload(serializationBuffer);
        return Error.Ok;
    }
    public override void _SetTargetPeer(int pPeer) => _sendTarget = pPeer;
    public override void _SetTransferMode(TransferModeEnum pMode) => _transferMode = pMode;
    public override void _SetTransferChannel(int pChannel) => _transferChannel = pChannel;
    public override TransferModeEnum _GetTransferMode() => _transferMode;
    public override int _GetTransferChannel() => _transferChannel;
    
    public override int _GetAvailablePacketCount() => _packetQueue.Count;
    public override byte[] _GetPacketScript() => _packetQueue.Dequeue().Payload;
    public override int _GetPacketPeer() => _packetQueue.Peek().ClientId;
    public override TransferModeEnum _GetPacketMode() => _packetQueue.Peek().Mode;
    public override int _GetPacketChannel() => _packetQueue.Peek().TransferChannel;
}