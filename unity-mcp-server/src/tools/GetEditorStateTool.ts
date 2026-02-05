import { UnityConnection } from "../communication/UnityConnection.js";

export interface UnityEditorState {
  activeGameObjects: string[];
  selectedObjects: string[];
  playModeState: string;
  sceneHierarchy: any;
  projectStructure: {
    scenes?: string[];
    assets?: string[];
    [key: string]: string[] | undefined;
  };
}

export interface UnityEditorStateHandler {
  resolve: (value: UnityEditorState) => void;
  reject: (reason?: any) => void;
}

// Command state management
let unityEditorStatePromise: UnityEditorStateHandler | null = null;

// Method to resolve the editor state - called when results arrive from Unity
export function resolveUnityEditorState(result: UnityEditorState): void {
  if (unityEditorStatePromise) {
    unityEditorStatePromise.resolve(result);
    unityEditorStatePromise = null;
  }
}

/**
 * Retrieve the current state of the Unity Editor.
 */
export async function getEditorState(
  format: string,
  unityConnection: UnityConnection
): Promise<{ content: { type: "text"; text: string }[] }> {
  const validFormats = ["Raw"];

  if (format && !validFormats.includes(format)) {
    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify({
            error: `Invalid format: "${format}". Valid formats are: ${validFormats.join(", ")}`,
            status: "error",
          }),
        },
      ],
    };
  }

  try {
    // Send command to Unity to get editor state
    unityConnection.sendMessage("getEditorState", {});

    // Wait for result with timeout handling
    const timeoutMs = 60_000;
    const editorState = await Promise.race([
      new Promise<UnityEditorState>((resolve, reject) => {
        unityEditorStatePromise = { resolve, reject };
      }),
      new Promise<never>((_, reject) =>
        setTimeout(
          () =>
            reject(
              new Error(
                `Getting editor state timed out after ${timeoutMs / 1000
                } seconds. This may indicate an issue with the Unity Editor.`
              )
            ),
          timeoutMs
        )
      ),
    ]);

    // Process the response based on format
    let responseData: any;
    switch (format) {
      case "Raw":
      default:
        responseData = editorState;
        break;
    }

    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify(responseData, null, 2),
        },
      ],
    };
  } catch (error) {
    let errorMessage = "Unknown error";

    if (error instanceof Error) {
      errorMessage = error.message;
    }

    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify({
            error: `Failed to get editor state: ${errorMessage}`,
            status: "error",
          }),
        },
      ],
    };
  }
}
