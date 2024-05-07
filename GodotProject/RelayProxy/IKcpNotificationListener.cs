using System;

namespace GodotMultiplayerExperiment.RelayProxyV2;

internal interface IKcpNotificationListener
{
    void NotifyPayload(int clientId, in ReadOnlySpan<byte> payload);
    void EmitPeerConnected(int clientId);
    void EmitPeerDisconnected(int clientId);
    void Cleanup();
}