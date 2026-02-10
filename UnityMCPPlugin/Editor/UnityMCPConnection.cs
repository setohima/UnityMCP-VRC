using UnityEngine;
using UnityEditor;
using System;
using System.Net.WebSockets;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Microsoft.CSharp;
using System.CodeDom.Compiler;

namespace UnityMCP.Editor
{
    [InitializeOnLoad]
    public class UnityMCPConnection
    {
        private static ClientWebSocket webSocket;
        private static bool isConnected = false;
        private static readonly Uri serverUri = new Uri("ws://localhost:8080");
        private static readonly Uri healthCheckUri = new Uri("http://localhost:8081/health");
        private static string lastErrorMessage = "";
        private static readonly Queue<LogEntry> logBuffer = new Queue<LogEntry>();
        private static readonly int maxLogBufferSize = 1000;
        private static bool isLoggingEnabled = true;
        private static EditorStateReporter editorStateReporter;
        private static InspectorDataReporter inspectorDataReporter;
        private static ScreenshotCapturer screenshotCapturer;
        private static SceneManipulator sceneManipulator;
        private static AssetManager assetManager;
        private static HttpClient httpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(2) };
        private static bool isSendingLog = false;
        private static bool isConnecting = false;

        // Public properties for the debug window
        public static bool IsConnected => isConnected && webSocket != null && webSocket.State == WebSocketState.Open;
        public static Uri ServerUri => serverUri;
        public static string LastErrorMessage => lastErrorMessage;
        public static bool IsLoggingEnabled
        {
            get => isLoggingEnabled;
            set
            {
                isLoggingEnabled = value;
                if (value)
                {
                    Application.logMessageReceived += HandleLogMessage;
                }
                else
                {
                    Application.logMessageReceived -= HandleLogMessage;
                }
            }
        }

        private class LogEntry
        {
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public LogType Type { get; set; }
            public DateTime Timestamp { get; set; }
        }

        // Public method to manually retry connection
        public static void RetryConnection()
        {
            Debug.Log("[UnityMCP] Manually retrying connection...");
            ConnectToServer();
        }
        private static readonly CancellationTokenSource cts = new CancellationTokenSource();

        // Constructor called on editor startup
        static UnityMCPConnection()
        {
            // Start capturing logs before anything else
            Application.logMessageReceived += HandleLogMessage;
            isLoggingEnabled = true;

            Debug.Log("[UnityMCP] Plugin initialized");
            EditorApplication.delayCall += () =>
            {
                Debug.Log("[UnityMCP] Starting initial connection");
                ConnectToServer();
            };
            EditorApplication.update += Update;
        }

        private static void HandleLogMessage(string message, string stackTrace, LogType type)
        {
            if (!isLoggingEnabled) return;

            var logEntry = new LogEntry
            {
                Message = message,
                StackTrace = stackTrace,
                Type = type,
                Timestamp = DateTime.UtcNow
            };

            lock (logBuffer)
            {
                logBuffer.Enqueue(logEntry);
                while (logBuffer.Count > maxLogBufferSize)
                {
                    logBuffer.Dequeue();
                }
            }

            // Send log to server if connected
            if (isConnected && webSocket?.State == WebSocketState.Open)
            {
                SendLogToServer(logEntry);
            }
        }

        private static async void SendLogToServer(LogEntry logEntry)
        {
            if (isSendingLog) return;
            isSendingLog = true;
            try
            {
                var message = JsonConvert.SerializeObject(new
                {
                    type = "log",
                    data = new
                    {
                        message = logEntry.Message,
                        stackTrace = logEntry.StackTrace,
                        logType = logEntry.Type.ToString(),
                        timestamp = logEntry.Timestamp
                    }
                });

                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
            }
            catch (Exception e)
            {
                // ForceDisconnect before logging to prevent recursive loop
                // (Debug.LogError -> HandleLogMessage -> SendLogToServer -> ...)
                ForceDisconnect();
                Debug.LogError($"[UnityMCP] Failed to send log to server: {e.Message}");
            }
            finally
            {
                isSendingLog = false;
            }
        }

        public static string[] GetRecentLogs(LogType[] types = null, int count = 100)
        {
            lock (logBuffer)
            {
                var logs = logBuffer.ToArray()
                    .Where(log => types == null || types.Contains(log.Type))
                    .TakeLast(count)
                    .Select(log => $"[{log.Timestamp:yyyy-MM-dd HH:mm:ss}] [{log.Type}] {log.Message}")
                    .ToArray();
                return logs;
            }
        }

        private static async Task<bool> CheckServerHealth()
        {
            try
            {
                var response = await httpClient.GetAsync(healthCheckUri);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var healthData = JsonConvert.DeserializeObject<Dictionary<string, object>>(content);

                    Debug.Log($"[UnityMCP] Health check passed - Server version: {healthData["version"]}, Uptime: {healthData["uptime"]}s");
                    return true;
                }
                else
                {
                    Debug.LogWarning($"[UnityMCP] Health check returned status: {response.StatusCode}");
                    return false;
                }
            }
            catch (HttpRequestException httpEx)
            {
                lastErrorMessage = "[UnityMCP] Cannot reach MCP Server - Server may not be running\n" +
                                   $"Please start the MCP server with: npx unity-mcp\n" +
                                   $"Details: {httpEx.Message}";
                Debug.LogError(lastErrorMessage);
                return false;
            }
            catch (TaskCanceledException)
            {
                lastErrorMessage = "[UnityMCP] Health check timed out - Server may be unresponsive";
                Debug.LogError(lastErrorMessage);
                return false;
            }
            catch (Exception ex)
            {
                lastErrorMessage = $"[UnityMCP] Health check failed: {ex.Message}";
                Debug.LogError(lastErrorMessage);
                return false;
            }
        }

        private static async Task SendHandshakeMessage()
        {
            try
            {
                var handshakeMessage = JsonConvert.SerializeObject(new
                {
                    type = "hello",
                    data = new
                    {
                        version = "1.0.0",
                        unityVersion = Application.unityVersion,
                        platform = Application.platform.ToString(),
                        timestamp = DateTime.UtcNow
                    }
                });

                var buffer = Encoding.UTF8.GetBytes(handshakeMessage);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
                Debug.Log("[UnityMCP] Handshake message sent");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Failed to send handshake: {e.Message}");
                throw;
            }
        }

        private static async void ConnectToServer()
        {
            // Prevent concurrent connection attempts (including during health check phase)
            if (isConnecting || IsConnected)
            {
                return;
            }

            isConnecting = true;
            try
            {
                // Perform health check before attempting WebSocket connection
                Debug.Log($"[UnityMCP] Performing health check at {healthCheckUri}");
                bool healthCheckPassed = await CheckServerHealth();

                if (!healthCheckPassed)
                {
                    Debug.LogWarning("[UnityMCP] Health check failed - Skipping connection attempt");
                    isConnected = false;
                    return;
                }

                Debug.Log($"[UnityMCP] Attempting to connect to MCP Server at {serverUri}");

                webSocket = new ClientWebSocket();
                webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(60);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

                await webSocket.ConnectAsync(serverUri, linkedCts.Token);

                // Send initial handshake
                Debug.Log("[UnityMCP] Sending initial handshake...");
                await SendHandshakeMessage();

                isConnected = true;
                lastPongReceived = DateTime.UtcNow; // Initialize heartbeat tracking
                Debug.Log("[UnityMCP] Successfully connected to MCP Server");
                StartReceiving();

                // Initialize editor state reporter
                editorStateReporter = new EditorStateReporter();
                inspectorDataReporter = new InspectorDataReporter();
                screenshotCapturer = new ScreenshotCapturer();
                sceneManipulator = new SceneManipulator();
                assetManager = new AssetManager();
            }
            catch (OperationCanceledException)
            {
                lastErrorMessage = "[UnityMCP] Connection attempt timed out - Server accepted connection but didn't respond";
                Debug.LogError(lastErrorMessage);
                CleanupFailedConnection();
            }
            catch (WebSocketException we)
            {
                // Detailed error code analysis
                string errorDetail = "";
                int nativeError = we.NativeErrorCode;

                // Common Windows Winsock error codes
                if (nativeError == 10061) // WSAECONNREFUSED
                {
                    errorDetail = "Connection refused - WebSocket server may not be listening on port 8080";
                }
                else if (nativeError == 10048) // WSAEADDRINUSE
                {
                    errorDetail = "Port 8080 is already in use by another application";
                }
                else if (nativeError == 10060) // WSAETIMEDOUT
                {
                    errorDetail = "Connection timed out - Server is not responding";
                }
                else if (nativeError == 10051) // WSAENETUNREACH
                {
                    errorDetail = "Network is unreachable";
                }
                else if (nativeError == 10065) // WSAEHOSTUNREACH
                {
                    errorDetail = "Host is unreachable";
                }
                else
                {
                    errorDetail = $"WebSocket error code: {we.WebSocketErrorCode}, Native error: {nativeError}";
                }

                lastErrorMessage = $"[UnityMCP] WebSocket connection failed\n" +
                                   $"Error: {errorDetail}\n" +
                                   $"Message: {we.Message}";

                if (we.InnerException != null)
                {
                    lastErrorMessage += $"\nInner Exception: {we.InnerException.Message}";
                }

                Debug.LogError(lastErrorMessage);
                CleanupFailedConnection();
            }
            catch (Exception e)
            {
                lastErrorMessage = $"[UnityMCP] Unexpected error during connection\n" +
                                   $"Type: {e.GetType().Name}\n" +
                                   $"Message: {e.Message}";
                Debug.LogError(lastErrorMessage);
                CleanupFailedConnection();
            }
            finally
            {
                isConnecting = false;
            }
        }

        private static void CleanupFailedConnection()
        {
            isConnected = false;
            if (webSocket != null)
            {
                try
                {
                    webSocket.Dispose();
                }
                catch { }
                webSocket = null;
            }
        }

        private static double lastReconnectAttemptTime = 0;
        private static readonly double reconnectInterval = 5.0;

        // Heartbeat settings
        private static double lastHeartbeatTime = 0;
        private static readonly double heartbeatInterval = 10.0; // Send ping every 10 seconds
        private static DateTime lastPongReceived = DateTime.MinValue;
        private static readonly double heartbeatTimeout = 20.0; // Consider dead if no pong for 20 seconds

        private static void Update()
        {
            double now = EditorApplication.timeSinceStartup;

            // Use the strict IsConnected property for reconnection logic
            if (!IsConnected)
            {
                if (now - lastReconnectAttemptTime >= reconnectInterval)
                {
                    Debug.Log("[UnityMCP] Attempting to reconnect...");
                    ConnectToServer();
                    lastReconnectAttemptTime = now;
                }
            }
            else
            {
                // Send heartbeat ping
                if (now - lastHeartbeatTime >= heartbeatInterval)
                {
                    SendHeartbeat();
                    lastHeartbeatTime = now;
                }

                // Check for heartbeat timeout
                if (lastPongReceived != DateTime.MinValue)
                {
                    var timeSinceLastPong = (DateTime.UtcNow - lastPongReceived).TotalSeconds;
                    if (timeSinceLastPong > heartbeatTimeout)
                    {
                        Debug.LogWarning("[UnityMCP] Heartbeat timeout - connection appears dead, forcing disconnect");
                        ForceDisconnect();
                    }
                }
            }
        }

        private static async void StartReceiving()
        {
            var buffer = new byte[1024 * 4];
            var messageBuffer = new List<byte>();
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        // Add received data to our message buffer
                        messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
                        
                        // If this is the end of the message, process it
                        if (result.EndOfMessage)
                        {
                            var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                            await HandleMessage(message);
                            messageBuffer.Clear();
                        }
                        // Otherwise, continue receiving the rest of the message
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Handle close message
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                        isConnected = false;
                        Debug.Log("[UnityMCP] WebSocket connection closed normally");
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error receiving message: {e.Message}");
                isConnected = false;
            }
        }

        private static async Task HandleMessage(string message)
        {
            try
            {
                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(message);
                if (data == null || !data.ContainsKey("type"))
                {
                    Debug.LogWarning($"[UnityMCP] Received invalid message (missing 'type'): {message}");
                    return;
                }

                string messageType = data["type"].ToString();

                switch (messageType)
                {
                    case "welcome":
                        Debug.Log($"[UnityMCP] Received welcome from server: {(data.ContainsKey("data") ? data["data"] : "")}");
                        break;
                    case "executeEditorCommand":
                        if (!data.ContainsKey("data")) { Debug.LogWarning("[UnityMCP] executeEditorCommand missing 'data'"); break; }
                        await EditorCommandExecutor.ExecuteEditorCommand(webSocket, cts.Token, data["data"].ToString());
                        break;
                    case "getEditorState":
                        await editorStateReporter.SendEditorState(webSocket, cts.Token);
                        break;
                    case "getGameObjectDetails":
                        if (!data.ContainsKey("data")) { Debug.LogWarning("[UnityMCP] getGameObjectDetails missing 'data'"); break; }
                        await inspectorDataReporter.SendObjectDetails(webSocket, cts.Token, data["data"].ToString());
                        break;
                    case "takeScreenshot":
                        await screenshotCapturer.SendScreenshot(webSocket, cts.Token);
                        break;
                    case "manipulateScene":
                        if (!data.ContainsKey("data")) { Debug.LogWarning("[UnityMCP] manipulateScene missing 'data'"); break; }
                        Debug.Log("[UnityMCP] Handling manipulateScene");
                        await sceneManipulator.HandleSceneManipulation(webSocket, cts.Token, data["data"].ToString());
                        break;
                    case "manageAssets":
                        if (!data.ContainsKey("data")) { Debug.LogWarning("[UnityMCP] manageAssets missing 'data'"); break; }
                        await assetManager.HandleAssetManagement(webSocket, cts.Token, data["data"].ToString());
                        break;
                    case "pong":
                        lastPongReceived = DateTime.UtcNow;
                        break;
                    default:
                        Debug.LogWarning($"[UnityMCP] Unknown message type: {messageType}");
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Error handling message: {e.Message}");
            }
        }

        private static async void SendHeartbeat()
        {
            if (!IsConnected)
                return;

            try
            {
                var message = JsonConvert.SerializeObject(new
                {
                    type = "ping",
                    data = new { timestamp = DateTime.UtcNow }
                });

                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityMCP] Failed to send heartbeat: {e.Message}");
                ForceDisconnect();
            }
        }

        private static void ForceDisconnect()
        {
            Debug.LogWarning("[UnityMCP] Forcing disconnect due to connection issues");
            isConnected = false;
            if (webSocket != null)
            {
                try
                {
                    webSocket.Abort();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[UnityMCP] Error aborting WebSocket: {e.Message}");
                }
                webSocket = null;
            }
            lastPongReceived = DateTime.MinValue;
        }
    }
}