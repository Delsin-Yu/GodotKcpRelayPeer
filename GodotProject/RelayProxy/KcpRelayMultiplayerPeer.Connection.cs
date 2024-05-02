using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using Godot;
using GodotTask;
using kcp2k;
using MemoryPack;
using HttpClient = System.Net.Http.HttpClient;

namespace GodotMultiplayerExperiment.RelayProxyV2;

public partial class KcpRelayMultiplayerPeer : IKcpNotificationListener
{
    private const string SESSION_HTTP_ROUTE = "session";
    private const string LIST_HTTP_GET = "list";
    private const string ALLOCATE_HTTP_GET = "allocate";
    private const string JOIN_HTTP_GET = "join";
    private const string MODIFY_HTTP_POST = "modify";

    private const bool USE_HTTPS = false;
    private const string HTTP_HEADER = USE_HTTPS ? "https" : "http";
    private const string HTTP_MODULE = "kcp-http";
    private const string HTTP_SERVER_ADDRESS = "127.0.0.1";
    private const ushort HTTP_PORT = 9000;
    private const string KCP_SERVER_ADDRESS = "127.0.0.1";
    private const ushort KCP_PORT = 9001;
    
    private readonly HttpClient _httpClient;
    private readonly Uri _listUri;
    private readonly Uri _allocateUri;
    private readonly Uri _joinUri;
    private readonly Uri _modifyUri;

    private static Action<string> _onLog;
    private static Action<string> _onLogFail;

    private KcpRelayClient _client;
    
    /// <summary>DualMode；同时监听 IPv6 和 IPv4。如果平台仅支持 IPv4，请禁用此选项</summary>
    private const bool _dualMode = true;

    /// <summary>启用 NoDelay 以减少延迟；这也可以更好地缩放，防止缓冲区满</summary>
    private const bool _noDelay = true;

    /// <summary>KCP内部更新间隔；100ms 是 KCP 的默认值，但建议使用更低的间隔以最小化延迟并扩展到更多网络实体</summary>
    private const int _interval = 10;

    /// <summary>KCP超时时间（毫秒）；请注意，KCP 会自动发送 ping。</summary>
    private const int _timeout = 10000;

    /// <summary>套接字接收缓冲区大小；大缓冲区有助于支持更多连接。如有需要，请增加操作系统套接字缓冲区大小限制</summary>
    private const int _recvBufferSize = 1024 * 1027 * 7;

    /// <summary>套接字发送缓冲区大小；大缓冲区有助于支持更多连接。如有需要，请增加操作系统套接字缓冲区大小限制</summary>
    private const int _sendBufferSize = 1024 * 1027 * 7;

    /// <summary>KCP fast resend 参数；更快的重发以换取更高的带宽成本。在正常模式下为 0，在Turbo模式下为2</summary>
    private const int _fastResend = 2;

    /// <summary>KCP窗口大小；修改以支持更高的负载。这也会增加最大消息大小</summary>
    private const uint _receiveWindowSize = 4096; //Kcp.WND_RCV; 128 by default. Mirror sends a lot, so we need a lot more.

    /// <summary>KCP窗口大小；修改以支持更高的负载。</summary>
    private const uint _sendWindowSize = 4096; //Kcp.WND_SND; 32 by default. Mirror sends a lot, so we need a lot more.

    /// <summary>在断开连接之前，KCP将尝试重新传输最多 MaxRetransmit（也称为 dead_link）个丢失的消息</summary>
    private const int _maxRetransmit = Kcp.DEADLINK * 2; // 默认情况下会过早地断开很多人的连接 (#3022)。使用 2 倍。
    
    public static void SetupLog(Action<string> onLog, Action<string> onLogFail)
    {
        _onLog = onLog;
        _onLogFail = onLogFail;
        kcp2k.Log.Info = static message => _onLog($"{DateTime.Now:mm:ss:fff}[color=cyan]{message}");
        kcp2k.Log.Warning = static message => _onLog($"{DateTime.Now:mm:ss:fff}[color=yellow]{message}");
        kcp2k.Log.Error = static message => _onLogFail(message);
    }

    public KcpRelayMultiplayerPeer()
    {
        // var fullHttpsServerUri = $"{HTTP_HEADER}://{HTTP_SERVER_ADDRESS}:{HTTP_PORT}/{HTTP_MODULE}/";
        var fullHttpsServerUri = $"{HTTP_HEADER}://{HTTP_SERVER_ADDRESS}:{HTTP_PORT}";
        _listUri = new($"{fullHttpsServerUri}/{SESSION_HTTP_ROUTE}/{LIST_HTTP_GET}");
        _allocateUri = new($"{fullHttpsServerUri}/{SESSION_HTTP_ROUTE}/{ALLOCATE_HTTP_GET}");
        _joinUri = new($"{fullHttpsServerUri}/{SESSION_HTTP_ROUTE}/{JOIN_HTTP_GET}");
        _modifyUri = new($"{fullHttpsServerUri}/{SESSION_HTTP_ROUTE}/{MODIFY_HTTP_POST}");   
        _httpClient = new();
    }
    
    private static void Log(string module, string message) => kcp2k.Log.Info($"[Relay] <{module}> {message}");
    private static void LogFail(string module, string message) => kcp2k.Log.Error($"Fail! [Relay] <{module}> {message}");

    public async GDTask<ResultContainer<SessionPreviewModelArray, Response>> ListSessions()
    {
        Log("ListSessions", "http request sent");
        var resultContainer = await TrySendHttpGetRequestAsync<SessionPreviewModelArray>(_listUri, "session list");
        Log("ListSessions", "http request returned");
        return resultContainer;
    }

    public async GDTask<Response> CreateSession(SessionInfoModel sessionInfoModel)
    {
        _connectionStatus = ConnectionStatus.Connecting;
        var connectionResult = await ConnectRelayServerAsync(
            _allocateUri,
            sessionInfoModel,
            AuthType.CreateSession,
            "session info",
            "Unable to Allocate Session",
            this
        );

        if (!connectionResult.TryGetValue(out _client, out var error))
        {
            _connectionStatus = ConnectionStatus.Disconnected;
            return error;
        }

        _connectionStatus = ConnectionStatus.Connected;
        return Response.FromSuccess();
    }

    public async GDTask<Response> JoinSession(ulong sessionId)
    {
        _connectionStatus = ConnectionStatus.Connecting;
        var connectionResult = await ConnectRelayServerAsync(
            _joinUri,
            sessionId,
            AuthType.JoinSession,
            "session info",
            "Unable to Join Session",
            this
        );
        
        if (!connectionResult.TryGetValue(out _client, out var error))
        {
            _connectionStatus = ConnectionStatus.Disconnected;
            return error;
        }
        
        _connectionStatus = ConnectionStatus.Connected;
        return Response.FromSuccess();
    }

    private readonly byte[] _tokenSerializationBuffer = new byte[16];

    private async GDTask<ResultContainer<KcpRelayClient, Response>> ConnectRelayServerAsync<T>(
        Uri uri,
        T value,
        AuthType authType,
        string parameterName,
        string tokenNullErrorKey,
        IKcpNotificationListener listener
    )
    {
        var serializationResult = TrySerializeTarget(value, parameterName);

        if (!serializationResult.TryGetValue(out var serializationBuffer, out var serializationError))
            return ResultContainer<KcpRelayClient, Response>.FromError(serializationError);
        
        var httpResult = await TrySendHttpPostRequestAsync(uri, serializationBuffer);

        if (!httpResult.TryGetValue(out var tokenModel, out var httpError))
        {
            return ResultContainer<KcpRelayClient, Response>.FromError(httpError);
        }
        
        if (!tokenModel.HasToken)
        {
            LogFail("ConnectRelayServerAsync", $"Server returned an invalid token model with error message: {tokenModel.ErrorMessage}");
            
            return ResultContainer<KcpRelayClient, Response>.FromError(Response.FromFail($"{tokenNullErrorKey} : {tokenModel.ErrorMessage}", null));
        }
        
        var kcpConnectionResult = await EstablishKcpRelayServerConnection(
            KCP_SERVER_ADDRESS,
            KCP_PORT,
            authType == AuthType.CreateSession ? RelayRole.Host : RelayRole.Client,
            listener
        );

        if (!kcpConnectionResult.TryGetValue(out var kcpRelayClient, out var kcpConnectionError))
        {
            return ResultContainer<KcpRelayClient, Response>.FromError(kcpConnectionError);
        }

        tokenModel.Token.TryWriteBytes(_tokenSerializationBuffer);

        var authResult = await kcpRelayClient.SendKcpAuthConnectionAsync(authType, _tokenSerializationBuffer, new CancellationTokenSource(_timeout).Token);

        if (!authResult.Success)
        {
            LogFail("ConnectRelayServerAsync", $"Kcp Authentication failed: {authResult.Message}/{authResult.Exception?.ToString() ?? "No Exception Thrown"}");
            
            return ResultContainer<KcpRelayClient, Response>.FromError(authResult);
        }

        Log("ConnectRelayServerAsync", $"Successfully Connected to relay server via Kcp");
        
        return ResultContainer<KcpRelayClient, Response>.FromSuccess(kcpRelayClient);
    }
    
    private static async GDTask<ResultContainer<KcpRelayClient, Response>> EstablishKcpRelayServerConnection(string serverAddress, ushort port, RelayRole role, IKcpNotificationListener listener)
    {
        var kcpClient = new KcpRelayClient(
            serverAddress,
            port,
            role,
            new(
                DualMode: _dualMode,
                NoDelay: _noDelay,
                Interval: _interval,
                Timeout: _timeout,
                RecvBufferSize: _recvBufferSize,
                SendBufferSize: _sendBufferSize,
                FastResend: _fastResend,
                ReceiveWindowSize: _receiveWindowSize,
                SendWindowSize: _sendWindowSize,
                MaxRetransmits: _maxRetransmit
            ),
            listener
        );

        var connectionResult = await kcpClient.InitKcpConnection();

        if (!connectionResult.Success)
        {
            LogFail("EstablishKcpRelayServerConnection", "Failed to establish Kcp connection");

            return ResultContainer<KcpRelayClient, Response>.FromError(connectionResult);
        }

        Log("EstablishKcpRelayServerConnection", "Kcp connection successfully established");
            
        return ResultContainer<KcpRelayClient, Response>.FromSuccess(kcpClient);
    }
    
    private async GDTask<ResultContainer<T, Response>> TrySendHttpGetRequestAsync<T>(Uri uri, string resultName)
    {
        HttpResponseMessage httpResponse;

        try
        {
            httpResponse = await _httpClient.GetAsync(uri);
        }
        catch (Exception e)
        {
            LogFail("TrySendHttpGetRequestAsync", $"GetAsync throws {e.GetType().Name}, {e.Message}");
            return ResultContainer<T, Response>.FromError(Response.FromFail("Error when sending http request to server", e));
        }
        
        Log("TrySendHttpGetRequestAsync", "Successfully retrieved the http response");

        var bodyExtractionResult = await TryExtractResponseBodyAsync(httpResponse);

        return !bodyExtractionResult.TryGetValue(out var buffer, out var error) ?
            ResultContainer<T, Response>.FromError(error) :
            TryDeserializeTarget<T>(buffer, resultName);
    }
    
    private async GDTask<ResultContainer<TokenModel, Response>> TrySendHttpPostRequestAsync(Uri uri, ArraySegment<byte> payload)
    {
        HttpResponseMessage httpResponse;

        using var request = new HttpRequestMessage();
        request.Content = new ByteArrayContent(payload.ToArray());
        request.Method = HttpMethod.Post;
        request.RequestUri = uri;
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/binary");

        try
        {
            httpResponse = await _httpClient.SendAsync(request);
        }
        catch (Exception e)
        {
            LogFail("TrySendHttpGetRequestAsync", $"SendAsync throws {e.GetType().Name}, {e.Message}");
            return ResultContainer<TokenModel, Response>.FromError(Response.FromFail("Error when sending http request to server", e));
        }

        Log("TrySendHttpGetRequestAsync", "Successfully retrieved the http response");
        
        var bodyExtractionResult = await TryExtractResponseBodyAsync(httpResponse);
        if (!bodyExtractionResult.TryGetValue(out var buffer, out var extractResponseBodyError))
        {
            return ResultContainer<TokenModel, Response>.FromError(extractResponseBodyError);
        }

        var deserializationResult = TryDeserializeTarget<TokenModel>(buffer, "token");
        if (!deserializationResult.TryGetValue(out var tokenModel, out var deserializationError))
        {
            return ResultContainer<TokenModel, Response>.FromError(deserializationError);
        }

        return ResultContainer<TokenModel, Response>.FromSuccess(tokenModel);
    }
    
    private static async GDTask<ResultContainer<byte[], Response>> TryExtractResponseBodyAsync(HttpResponseMessage responseMessage)
    {
        if (responseMessage.StatusCode != HttpStatusCode.OK)
        {
            LogFail("TryExtractResponseBodyAsync", $"Http status code is {responseMessage.StatusCode}, failing");

            return ResultContainer<byte[], Response>
                .FromError(Response.FromFail($"Server reject the request: {responseMessage.StatusCode}", null));
        }

        var binary = await responseMessage.Content.ReadAsByteArrayAsync();
        
        Log("TryExtractResponseBodyAsync", $"Successfully retrieved {binary.Length} bytes binary of http response message");
        
        return ResultContainer<byte[], Response>.FromSuccess(binary);
    }
    
    private static ResultContainer<ArraySegment<byte>, Response> TrySerializeTarget<T>(in T value, string valueName)
    {
        byte[] binary;
        try
        {
            binary = MemoryPackSerializer.Serialize(value);
        }
        catch (Exception e)
        {
            LogFail("TrySerializeTarget", $"{e.GetType().Name} when serializing {typeof(T).Name} to binary, {e.Message}\n{value}");
            
            return ResultContainer<ArraySegment<byte>, Response>
                .FromError(Response.FromFail($"Error when serializing {valueName}", e));
        }

        Log("TrySerializeTarget", $"Successfully serialized {typeof(T).Name} to {binary.Length} bytes of binary:\n{value}");

        return ResultContainer<ArraySegment<byte>, Response>.FromSuccess(binary);
    }

    
    private static ResultContainer<T, Response> TryDeserializeTarget<T>(byte[] binary, string valueName)
    {
        T value;
        try
        {
            value = MemoryPackSerializer.Deserialize<T>(binary);
        }
        catch (Exception e)
        {
            LogFail("TrySerializeTarget", $"{e.GetType().Name} when deserializing {binary.Length} bytes of binary into {typeof(T).Name}, {e.Message}");
            
            return ResultContainer<T, Response>
                .FromError(Response.FromFail($"Error when deserializing {valueName} from server", e));
        }

        Log("TryDeserializeTarget", $"Successfully deserialized {binary.Length} bytes of binary to {typeof(T).Name}:\n{value}");
       
        return ResultContainer<T, Response>
            .FromSuccess(value);
    }
}