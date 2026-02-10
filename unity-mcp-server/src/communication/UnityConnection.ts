import { WebSocket, WebSocketServer } from "ws";
import { createServer, IncomingMessage, ServerResponse, Server } from "http";
import { CommandResult, resolveCommandResult } from "../tools/ExecuteEditorCommandTool.js";
import { LogEntry } from "../tools/index.js";
import { resolveUnityEditorState, UnityEditorState } from "../tools/GetEditorStateTool.js";
import { resolveObjectDetails } from "../tools/GetObjectDetailsTool.js";
import { resolveScreenshot } from "../tools/TakeScreenshotTool.js";
import { resolveSceneManipulation } from "../tools/ManipulateSceneTool.js";
import { resolveAssetManagement } from "../tools/ManageAssetsTool.js";

export class UnityConnection {
  private wsServer: WebSocketServer;
  private connection: WebSocket | null = null;
  private healthServer: Server;
  private readonly wsPort: number;
  private readonly healthPort: number;
  private readonly startTime: number;

  private logBuffer: LogEntry[] = [];
  private readonly maxLogBufferSize = 1000;

  // Event callbacks
  private onLogReceived: ((entry: LogEntry) => void) | null = null;

  constructor(port: number = 8080, healthPort: number = 8081) {
    this.wsPort = port;
    this.healthPort = healthPort;
    this.startTime = Date.now();
    this.wsServer = new WebSocketServer({ port });
    this.setupWebSocket();
    this.healthServer = this.setupHealthServer();
  }

  private setupWebSocket() {
    console.error(`[Unity MCP] WebSocket server starting on port ${this.wsPort}`);

    this.wsServer.on("listening", () => {
      console.error(
        `[Unity MCP] WebSocket server is listening on port ${this.wsPort}`,
      );
    });

    this.wsServer.on("error", (error: any) => {
      // Detailed error logging for common issues
      if (error.code === 'EADDRINUSE') {
        console.error(`[Unity MCP] ERROR: Port ${this.wsPort} is already in use. Please ensure no other instance is running.`);
      } else if (error.code === 'EACCES') {
        console.error(`[Unity MCP] ERROR: Permission denied for port ${this.wsPort}. Try using a port number > 1024.`);
      } else if (error.code === 'EADDRNOTAVAIL') {
        console.error(`[Unity MCP] ERROR: Address not available. Check your network configuration.`);
      } else {
        console.error(`[Unity MCP] WebSocket server error: ${error.code || 'UNKNOWN'}`, error.message);
      }
    });

    this.wsServer.on("connection", (ws: WebSocket) => {
      console.error("[Unity MCP] Unity Editor connected");
      this.connection = ws;

      ws.on("message", (data: Buffer) => {
        try {
          const message = JSON.parse(data.toString());
          console.error("[Unity MCP] Received message:", message.type);
          this.handleUnityMessage(message);
        } catch (error) {
          console.error("[Unity MCP] Error handling message:", error);
        }
      });

      ws.on("error", (error) => {
        console.error("[Unity MCP] WebSocket error:", error);
      });

      ws.on("close", () => {
        console.error("[Unity MCP] Unity Editor disconnected");
        this.connection = null;
      });
    });
  }

  private setupHealthServer(): Server {
    const server = createServer((req: IncomingMessage, res: ServerResponse) => {
      // Enable CORS for health check endpoint
      res.setHeader('Access-Control-Allow-Origin', '*');
      res.setHeader('Access-Control-Allow-Methods', 'GET, OPTIONS');
      res.setHeader('Access-Control-Allow-Headers', 'Content-Type');

      // Handle OPTIONS preflight request
      if (req.method === 'OPTIONS') {
        res.writeHead(200);
        res.end();
        return;
      }

      if (req.url === '/health' && req.method === 'GET') {
        const healthStatus = {
          status: 'healthy',
          version: '0.2.0',
          websocketPort: this.wsPort,
          healthPort: this.healthPort,
          uptime: Math.floor((Date.now() - this.startTime) / 1000),
          connected: this.connection !== null,
          timestamp: new Date().toISOString()
        };

        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(healthStatus, null, 2));
        console.error(`[Unity MCP] Health check requested - Status: ${this.connection ? 'connected' : 'waiting for connection'}`);
      } else {
        res.writeHead(404, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'Not Found', availableEndpoints: ['/health'] }));
      }
    });

    server.on('error', (error: any) => {
      if (error.code === 'EADDRINUSE') {
        console.error(`[Unity MCP] ERROR: Health check port ${this.healthPort} is already in use.`);
      } else {
        console.error(`[Unity MCP] Health server error: ${error.code || 'UNKNOWN'}`, error.message);
      }
    });

    server.listen(this.healthPort, () => {
      console.error(`[Unity MCP] Health check endpoint available at http://localhost:${this.healthPort}/health`);
    });

    return server;
  }

  private handleUnityMessage(message: any) {
    switch (message.type) {
      case "hello":
        this.handleHandshake(message.data);
        break;

      case "commandResult":
        resolveCommandResult(message.data as CommandResult);
        break;

      case "editorState":
        resolveUnityEditorState(message.data as UnityEditorState);
        break;

      case "objectDetails":
        resolveObjectDetails(message.data);
        break;

      case "screenshot":
        resolveScreenshot(message.data);
        break;

      case "sceneManipulationResult":
        resolveSceneManipulation(message.data);
        break;

      case "assetManagementResult":
        resolveAssetManagement(message.data);
        break;

      case "log":
        this.handleLogMessage(message.data);
        if (this.onLogReceived) {
          this.onLogReceived(message.data);
        }
        break;

      default:
        console.error("[Unity MCP] Unknown message type:", message.type);
    }
  }

  private handleHandshake(data: any) {
    console.error("[Unity MCP] Received handshake from Unity Editor");
    console.error(`[Unity MCP] Unity Version: ${data.unityVersion}, Platform: ${data.platform}`);

    // Send welcome response
    const welcomeMessage = {
      type: "welcome",
      data: {
        serverVersion: "0.2.0",
        features: [
          "execute_editor_command",
          "get_editor_state",
          "get_logs",
          "get_object_details",
          "take_screenshot",
          "manipulate_scene",
          "manage_assets"
        ],
        timestamp: new Date().toISOString()
      }
    };

    if (this.connection) {
      this.connection.send(JSON.stringify(welcomeMessage));
      console.error("[Unity MCP] Welcome message sent");
    }
  }

  private handleLogMessage(logEntry: LogEntry) {
    // Add to buffer, removing oldest if at capacity
    this.logBuffer.push(logEntry);
    if (this.logBuffer.length > this.maxLogBufferSize) {
      this.logBuffer.shift();
    }
  }

  // Public API
  public isConnected(): boolean {
    return this.connection !== null;
  }

  public getLogBuffer(): LogEntry[] {
    return [...this.logBuffer];
  }

  public setOnLogReceived(callback: (entry: LogEntry) => void): void {
    this.onLogReceived = callback;
  }

  public sendMessage(type: string, data: any): void {
    if (this.connection) {
      this.connection.send(JSON.stringify({ type, data }));
    } else {
      console.error(
        "[Unity MCP] Cannot send message: Unity Editor not connected",
      );
    }
  }

  public async waitForConnection(timeoutMs: number = 60000): Promise<boolean> {
    if (this.connection) return true;

    return new Promise<boolean>((resolve) => {
      const timeout = setTimeout(() => resolve(false), timeoutMs);

      const connectionHandler = () => {
        clearTimeout(timeout);
        this.wsServer.off("connection", connectionHandler);
        resolve(true);
      };

      this.wsServer.on("connection", connectionHandler);
    });
  }

  public close(): void {
    if (this.connection) {
      this.connection.close();
      this.connection = null;
    }
    this.wsServer.close();
    this.healthServer.close(() => {
      console.error("[Unity MCP] Health server closed");
    });
  }
}
