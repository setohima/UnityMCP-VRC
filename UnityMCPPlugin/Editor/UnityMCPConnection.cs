using UnityEngine;
using UnityEditor;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
        private static string lastErrorMessage = "";
        private static readonly Queue<LogEntry> logBuffer = new Queue<LogEntry>();
        private static readonly int maxLogBufferSize = 1000;
        private static bool isLoggingEnabled = true;
        private static EditorStateReporter editorStateReporter;
        private static InspectorDataReporter inspectorDataReporter;
        private static ScreenshotCapturer screenshotCapturer;
        private static SceneManipulator sceneManipulator;
        private static AssetManager assetManager;

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
                Debug.LogError($"[UnityMCP] Failed to send log to server: {e.Message}");
                // Connection is likely broken, force disconnect
                ForceDisconnect();
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

        private static async void ConnectToServer()
        {
            // Check both flag and actual WebSocket state
            if (IsConnected || (webSocket != null && webSocket.State == WebSocketState.Connecting))
            {
                // Debug.Log("[UnityMCP] Already connected or connecting");
                return;
            }

            try
            {
                Debug.Log($"[UnityMCP] Attempting to connect to MCP Server at {serverUri}");
                // Debug.Log($"[UnityMCP] Current Unity version: {Application.unityVersion}");
                // Debug.Log($"[UnityMCP] Current platform: {Application.platform}");

                webSocket = new ClientWebSocket();
                webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(60);

                var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

                await webSocket.ConnectAsync(serverUri, linkedCts.Token);
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
                lastErrorMessage = "[UnityMCP] Connection attempt timed out";
                Debug.LogError(lastErrorMessage);
                isConnected = false;
            }
            catch (WebSocketException we)
            {
                lastErrorMessage = $"[UnityMCP] WebSocket error: {we.Message}\nDetails: {we.InnerException?.Message}";
                Debug.LogError(lastErrorMessage);
                // Debug.LogError($"[UnityMCP] Stack trace: {we.StackTrace}");
                isConnected = false;
            }
            catch (Exception e)
            {
                lastErrorMessage = $"[UnityMCP] Failed to connect to MCP Server: {e.Message}\nType: {e.GetType().Name}";
                Debug.LogError(lastErrorMessage);
                // Debug.LogError($"[UnityMCP] Stack trace: {e.StackTrace}");
                isConnected = false;
            }
        }

        private static float reconnectTimer = 0f;
        private static readonly float reconnectInterval = 5f;

        // Heartbeat settings
        private static float heartbeatTimer = 0f;
        private static readonly float heartbeatInterval = 10f; // Send ping every 10 seconds
        private static DateTime lastPongReceived = DateTime.MinValue;
        private static readonly float heartbeatTimeout = 20f; // Consider dead if no pong for 20 seconds

        private static void Update()
        {
            // Use the strict IsConnected property for reconnection logic
            if (!IsConnected)
            {
                reconnectTimer += Time.deltaTime;
                if (reconnectTimer >= reconnectInterval)
                {
                    Debug.Log("[UnityMCP] Attempting to reconnect...");
                    ConnectToServer();
                    reconnectTimer = 0f;
                }
            }
            else
            {
                // Reset timer when connected
                reconnectTimer = 0f;

                // Send heartbeat ping
                heartbeatTimer += Time.deltaTime;
                if (heartbeatTimer >= heartbeatInterval)
                {
                    SendHeartbeat();
                    heartbeatTimer = 0f;
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
                switch (data["type"].ToString())
                {
                    case "executeEditorCommand":
                        await EditorCommandExecutor.ExecuteEditorCommand(webSocket, cts.Token, data["data"].ToString());
                        break;
                    case "getEditorState":
                        await editorStateReporter.SendEditorState(webSocket, cts.Token);
                        break;
                    case "getGameObjectDetails":
                        await inspectorDataReporter.SendObjectDetails(webSocket, cts.Token, data["data"].ToString());
                        break;
                    case "takeScreenshot":
                        await screenshotCapturer.SendScreenshot(webSocket, cts.Token);
                        break;
                    case "manipulateScene":
                        Debug.Log("[UnityMCP] Handling manipulateScene");
                        await sceneManipulator.HandleSceneManipulation(webSocket, cts.Token, data["data"].ToString());
                        break;
                    case "manageAssets":
                        await assetManager.HandleAssetManagement(webSocket, cts.Token, data["data"].ToString());
                        break;
                    case "pong":
                        // Update last pong received time
                        lastPongReceived = DateTime.UtcNow;
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error handling message: {e.Message}");
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