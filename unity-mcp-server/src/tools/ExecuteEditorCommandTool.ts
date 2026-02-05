import { UnityConnection } from "../communication/UnityConnection.js";
import { LogEntry } from "./types.js";

export interface CommandResult {
  result: any;
  logs: string[];
  errors: string[];
  warnings: string[];
  executionSuccess: boolean;
  errorDetails?: {
    message: string;
    stackTrace: string;
    type: string;
  };
}

export interface CommandResultHandler {
  resolve: (value: CommandResult) => void;
  reject: (reason?: any) => void;
}

// Command state management
let commandResultPromise: CommandResultHandler | null = null;
let commandStartTime: number | null = null;

// Method to resolve the command result - called when results arrive from Unity
export function resolveCommandResult(result: CommandResult): void {
  if (commandResultPromise) {
    commandResultPromise.resolve(result);
    commandResultPromise = null;
  }
}

/**
 * Execute arbitrary C# code within the Unity Editor context.
 */
export async function executeEditorCommand(
  code: string,
  unityConnection: UnityConnection
): Promise<{ content: { type: "text"; text: string }[] }> {
  // Validate code parameter
  if (!code || typeof code !== "string" || code.trim().length === 0) {
    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify({
            error: "The code parameter is required and cannot be empty",
            status: "error",
          }),
        },
      ],
    };
  }

  try {
    // Set command start time
    commandStartTime = Date.now();

    // Send command to Unity
    unityConnection.sendMessage("executeEditorCommand", {
      code: code,
    });

    // Wait for result with enhanced timeout handling
    const timeoutMs = 60_000;
    const result = await Promise.race([
      new Promise<CommandResult>((resolve, reject) => {
        commandResultPromise = { resolve, reject };
      }),
      new Promise<never>((_, reject) =>
        setTimeout(
          () =>
            reject(
              new Error(
                `Command execution timed out after ${timeoutMs / 1000
                } seconds. This may indicate a long-running operation or an issue with the Unity Editor.`
              )
            ),
          timeoutMs
        )
      ),
    ]);

    // Calculate execution time
    const executionTime = Date.now() - (commandStartTime || 0);

    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify(
            {
              result,
              executionTime: `${executionTime}ms`,
              status: "success",
            },
            null,
            2
          ),
        },
      ],
    };
  } catch (error) {
    // Enhanced error handling with specific error types
    let errorMessage = "Unknown error";

    if (error instanceof Error) {
      if (error.message.includes("timed out")) {
        errorMessage = error.message;
      } else if (error.message.includes("NullReferenceException")) {
        errorMessage = "The code attempted to access a null object. Please check that all GameObject references exist.";
      } else if (error.message.includes("CompileError")) {
        errorMessage = "C# compilation error. Please check the syntax of your code.";
      } else {
        errorMessage = error.message;
      }
    }

    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify({
            error: `Failed to execute command: ${errorMessage}`,
            status: "error",
          }),
        },
      ],
    };
  }
}
