using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GodotMultiplayerExperiment.RelayProxyV2;
using GodotTask;
using Helpers;

namespace GodotNetworkExperiment;

public partial class Main : Node
{
    [Export] private Control _controlParent;

    [ExportCategory("Relay")]
    
    [Export] private Button _listRoomsBtn;
    [Export] private Label _roomsLabel;
    
    [Export] private LineEdit _name;
    [Export] private SpinBox _maxRoom;
    [Export] private Button _allocate;
    
    [Export] private LineEdit _roomId;
    [Export] private Button _join;
    
    [ExportCategory("Direct")]
    
    [Export] private Button _asServer;
    [Export] private LineEdit _serverAddress;
    [Export] private Button _asClient;
    [Export] private Button _disconnect;
    [Export] private PackedScene _player;
    [Export] private Node2D _playerContainer;
    [Export] private Label _label;

    public static int LocalId { get; set; }
    private readonly Dictionary<int, PlayerController> _playerControllers = [];

    private Action _onClose;
    
    public override void _Ready()
    {
        KcpRelayMultiplayerPeer.SetupLog(GD.PrintRich, GD.PrintErr);

        _disconnect.Pressed += () => _onClose();

        _asServer.Pressed += () =>
        {
            if (TryCreateP2PHost(out var peer)) AsHost(peer);
        };
        _asClient.Pressed += () =>
        {
            if (TryCreateP2PClient(_serverAddress.Text, out var peer)) AsClient(peer);
        };

        _listRoomsBtn.Pressed += () => ListAsync(_roomsLabel).Forget();

        _allocate.Pressed += () =>
        {
            AllocateAsync(_name.Text, (int)_maxRoom.Value).ContinueWith(
                peer =>
                {
                    if (peer is null) return;
                    AsHost(peer);
                }
            );
        };
        _join.Pressed += () =>
        {
            JoinAsync(ulong.Parse(_roomId.Text)).ContinueWith(
                peer =>
                {
                    if (peer is null) return;
                    AsClient(peer);
                }
            );
        };
    }

    private async GDTask ListAsync(Label label)
    {
        _controlParent.Hide();
        var sessions = await new KcpRelayMultiplayerPeer().ListSessions();
        if (!sessions.TryGetValue(out var value, out var error))
        {
            GD.PrintErr(error.ToString());
            label.Text = error.ToString();
        }
        else
        {
            var currentSessions =
                $"Current Sessions: \n" +
                $"- - - - - - - - - -\n" +
                $"{string.Join('\n', value.Array.Select(x => x.ToString()).ToArray())}\n" +
                $"- - - - - - - - - -";
            
            GD.Print(currentSessions);

            label.Text = currentSessions;
        }
        _controlParent.Show();
    }
    
    private async GDTask<KcpRelayMultiplayerPeer> AllocateAsync(string sessionName, int maxRoom)
    {
        _controlParent.Hide();
        var peer = new KcpRelayMultiplayerPeer();
        var result = await peer.CreateSession(new(sessionName, maxRoom));
        _controlParent.Show();
        if (!result.Success)
        {
            GD.PrintErr($"AllocateAsync Error: {result}");
            return null;
        }
        return peer;
    }

    private async GDTask<KcpRelayMultiplayerPeer> JoinAsync(ulong sessionId)
    {
        _controlParent.Hide();
        var peer = new KcpRelayMultiplayerPeer();
        var result = await peer.JoinSession(sessionId);
        _controlParent.Show();
        if (!result.Success)
        {
            GD.PrintErr($"$JoinAsync Error: {result}");
            return null;
        }
        return peer;
    }
    
    private static bool TryCreateP2PHost(out ENetMultiplayerPeer peer)
    {
        peer = null;
        var newPeer = new ENetMultiplayerPeer();
        
        var port = 20000;

        while(true)
        {
            var code = newPeer.CreateServer(port);
            if (code == Error.Ok) break;
            if (port + 1 > ushort.MaxValue)
            {
                GD.PrintErr($"Create Server Failed! {code.ToString()}");
                newPeer.Free();
                return false;
            }
            port++;
        }

        GD.Print($"Port is {port}");
        peer = newPeer;
        return true;
    }

    private static bool TryCreateP2PClient(string addressString, out ENetMultiplayerPeer peer)
    {
        peer = null;

        var newPeer = new ENetMultiplayerPeer();
        var fullAddress = addressString.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var address = fullAddress[0];
        var port = Convert.ToUInt16(fullAddress[1]);
        var code = newPeer.CreateClient(address, port);
        if (code is not Error.Ok)
        {
            GD.PrintErr($"Create Client Failed! {code.ToString()}");
            return false;
        }

        peer = newPeer;
        return true;
    }

    private void AsHost(MultiplayerPeer peer)
    {
        var multiplayerApi = Multiplayer;
        multiplayerApi.MultiplayerPeer = peer;
        
        LocalId = peer.GetUniqueId();
        SpawnPlayer(LocalId);
        GD.Print("Host OK!");
        _label.Text = "Host";

        MultiplayerApi.PeerConnectedEventHandler peerConnected = peerId =>
        {
            var peerIdInt = (int)peerId;
            Rpc(MethodName.SpawnPlayer, ArgArray.Get([peerIdInt]));
            // var currentPeers = Multiplayer.GetPeers().ToList();
            // currentPeers.Remove(peerIdInt);
            // currentPeers.Add(LocalId);
            //
            // foreach (var currentConnectedPeerId in currentPeers)
            // {
            //     RpcId(peerId, MethodName.SpawnPlayer, ArgArray.Get([currentConnectedPeerId]));
            // }
        };
        multiplayerApi.PeerConnected += peerConnected;

        MultiplayerApi.PeerDisconnectedEventHandler peerDisconnected = peerId =>
        {
            GD.Print($"Peer {peerId} DCs");
            if (peerId == 0) return;
            var peerIdInt = (int)peerId;
            Rpc(MethodName.DeletePlayer, ArgArray.Get([peerIdInt]));
        };
        multiplayerApi.PeerDisconnected += peerDisconnected;
        
        _onClose = () =>
        {
            multiplayerApi.PeerConnected -= peerConnected;
            multiplayerApi.PeerDisconnected -= peerDisconnected;
            Cleanup();
            using var currentPeer = Multiplayer.MultiplayerPeer;
            currentPeer.Close();
            Multiplayer.MultiplayerPeer = null;
        };
    }
    
    private void AsClient(MultiplayerPeer peer)
    {
        var multiplayerApi = Multiplayer;
        multiplayerApi.MultiplayerPeer = peer;
        GD.Print("Client OK!");
        _label.Text = "Client";

        LocalId = peer.GetUniqueId();
    
        _onClose = () =>
        {
            multiplayerApi.ServerDisconnected -= _onClose;
            Cleanup();
            using var currentPeer = Multiplayer.MultiplayerPeer;
            currentPeer.Close();
            Multiplayer.MultiplayerPeer = null;
        };
        
        multiplayerApi.ServerDisconnected += _onClose;
    }

    private void Cleanup()
    {
        GD.Print("Perform Cleanup Start");
        
        foreach (var value in _playerControllers.Values)
        {
            value.Free();
            value.Dispose();
        }
        _playerControllers.Clear();
        
        GD.Print("Perform Cleanup Finish");
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SpawnPlayer(int networkId)
    {
        GD.Print($"Spawn Player for: {networkId}");
        var playerInstance = _player.Instantiate<PlayerController>();
        playerInstance.LocalId = networkId;
        playerInstance.SetMultiplayerAuthority(networkId);
        _playerContainer.AddChild(playerInstance);
        playerInstance.Name = $"Player-{networkId}";
        playerInstance.UpdateVisual();
        _playerControllers.Add(networkId, playerInstance);
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DeletePlayer(int networkId)
    {
        if (!_playerControllers.Remove(networkId, out var playerController)) throw new InvalidOperationException();
        playerController.Free();
        playerController.Dispose();
    }
}
