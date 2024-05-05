# Kcp Server Relay for Godot / .Net

> This project enables players under complex NAT environments to experience typical client-hosted/client-authoritative multiplayer design, by leveraging a centralized, high-performance `ASP.Net Core AOT` server to handle traffic relay.
> When using the project, game logic still runs on the host's computer, this puts lesser computational weight on the server, which should make this project a better candidate for small to mid-scaled studios compared to server-authoritative when choosing a multiplayer design.

#### This solution is shipped with two separate parts:
1. `GodotKcpServer`: An `ASP.Net Core AOT` server that handles room creation and traffic relay for players inside one room.
2. `GodotKcpPeer`: A sample Godot project that contains the `KcpRelayMultiplayerPeer`, which has the implementation of using the relay server.
3. `kcp2k-span`: A fork of `Kcp2k` that translates every `ArraySegment` usage into `Span` or `Memory` for better performance.

#### Run
1. Build and run the `KcpGameServer` C# project.
2. Open the `GodotKcpPeer` Godot Project, turn on multi-instance from the debug menu, and click play.
3. Use the `List Rooms` button to check the currently opened rooms on the server.
4. Use the `Create Room` button along with the arguments to create a room on the server.
5. Use the `Join Room` button along with the arguments to join a room on the server.
6. Alternatively, or for testing purposes, use the buttons in the `Direct` section for traditional `ENet` based Godot Multiplayer implementation.
