#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { UnityConnection } from "./communication/UnityConnection.js";
import { getAllResources, ResourceContext } from "./resources/index.js";
import { LogEntry } from "./tools/types.js";

// Tool Implementations
import { executeEditorCommand, CommandResult, resolveCommandResult } from "./tools/ExecuteEditorCommandTool.js";
import { getEditorState, UnityEditorState, resolveUnityEditorState } from "./tools/GetEditorStateTool.js";
import { getLogs } from "./tools/GetLogsTool.js";

// Re-export for UnityConnection
export { resolveCommandResult, resolveUnityEditorState };

class UnityMCPServer {
  private server: McpServer;
  private unityConnection: UnityConnection;
  private initialized = false;

  constructor() {
    // Initialize MCP Server
    this.server = new McpServer({
      name: "unity-mcp-server",
      version: "0.2.0",
    });

    // Initialize WebSocket Server for Unity communication
    this.unityConnection = new UnityConnection(8080);

    // Error handling
    process.on("SIGINT", async () => {
      await this.cleanup();
      process.exit(0);
    });
  }

  /** Initialize the server asynchronously */
  async initialize() {
    if (this.initialized) return;

    await this.setupResources();
    this.setupTools();

    this.initialized = true;
  }

  /** Optional resources the user can include in Claude Desktop to give additional context to the LLM */
  private async setupResources() {
    const resources = await getAllResources();

    // Register each resource
    for (const resource of resources) {
      const def = resource.getDefinition();
      this.server.resource(
        def.name,
        def.uri,
        {
          description: def.description,
          mimeType: def.mimeType,
        },
        async (uri) => {
          const resourceContext: ResourceContext = {
            unityConnection: this.unityConnection,
          };
          const content = await resource.getContents(resourceContext);
          return {
            contents: [
              {
                uri: uri.href,
                mimeType: def.mimeType,
                text: content,
              },
            ],
          };
        }
      );
    }
  }

  private setupTools() {
    const unityConnection = this.unityConnection;

    // Register execute_editor_command tool
    this.server.tool(
      "execute_editor_command",
      "Execute arbitrary C# code file within the Unity Editor context. This powerful tool allows for direct manipulation of the Unity Editor, GameObjects, components, and project assets using the Unity Editor API.",
      {
        code: z.string().min(1).describe(
          `C# code file to execute in the Unity Editor context.
The code has access to all UnityEditor and UnityEngine APIs.
Include any necessary using directives at the top of the code.
The code must have a EditorCommand class with a static Execute method that returns an object.`
        ),
      },
      async ({ code }) => {
        return await executeEditorCommand(code, unityConnection);
      }
    );

    // Register get_editor_state tool
    this.server.tool(
      "get_editor_state",
      "Retrieve the current state of the Unity Editor, including active GameObjects, selection state, play mode status, scene hierarchy, project structure, and assets. This tool provides a comprehensive snapshot of the editor's current context.",
      {
        format: z.enum(["Raw"]).default("Raw").optional().describe(
          "Specify the output format: Raw: Complete editor state including all available data"
        ),
      },
      async ({ format }) => {
        return await getEditorState(format || "Raw", unityConnection);
      }
    );

    // Register get_logs tool
    this.server.tool(
      "get_logs",
      "Retrieve and filter Unity Editor logs with comprehensive filtering options. This tool provides access to editor logs, console messages, warnings, errors, and exceptions with powerful filtering capabilities.",
      {
        types: z.array(z.enum(["Log", "Warning", "Error", "Exception"])).optional().describe(
          "Filter logs by type. If not specified, all types are included."
        ),
        count: z.number().min(1).max(1000).default(100).optional().describe(
          "Maximum number of log entries to return"
        ),
        fields: z.array(z.enum(["message", "stackTrace", "logType", "timestamp"])).optional().describe(
          "Specify which fields to include in the output."
        ),
        messageContains: z.string().min(1).optional().describe(
          "Filter logs to only include entries where the message contains this string (case-sensitive)"
        ),
        stackTraceContains: z.string().min(1).optional().describe(
          "Filter logs to only include entries where the stack trace contains this string (case-sensitive)"
        ),
        timestampAfter: z.string().optional().describe(
          "Filter logs after this ISO timestamp (inclusive)"
        ),
        timestampBefore: z.string().optional().describe(
          "Filter logs before this ISO timestamp (inclusive)"
        ),
      },
      async (args) => {
        const logBuffer = unityConnection.getLogBuffer();
        return getLogs(args, logBuffer);
      }
    );
  }

  private async cleanup() {
    this.unityConnection.close();
    await this.server.close();
  }

  async run() {
    await this.initialize();

    const transport = new StdioServerTransport();
    await this.server.connect(transport);
    console.error("Unity MCP server running on stdio");

    // Wait for WebSocket server to be ready
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 100); // Small delay to ensure WebSocket server is initialized
    });
  }
}

const server = new UnityMCPServer();
server.run().catch(console.error);
