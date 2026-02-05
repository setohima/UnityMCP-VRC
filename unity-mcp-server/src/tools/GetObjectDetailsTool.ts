import { Tool } from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";
import { UnityConnection } from "../communication/UnityConnection.js";

// Request/Response types matching C# implementation
interface ObjectDetails {
  name: string;
  active: boolean;
  tag: string;
  layer: string;
  transform: {
    position: { x: number; y: number; z: number };
    rotation: { x: number; y: number; z: number };
    scale: { x: number; y: number; z: number };
  };
  components: {
    type: string;
    data: Record<string, any>;
  }[];
}

interface ObjectDetailsHandler {
  resolve: (value: ObjectDetails) => void;
  reject: (reason?: any) => void;
}

// Queue for pending requests
let objectDetailsPromise: ObjectDetailsHandler | null = null;

export function resolveObjectDetails(result: any): void {
  if (objectDetailsPromise) {
    if (result.error) {
      objectDetailsPromise.reject(new Error(result.error));
    } else {
      objectDetailsPromise.resolve(result);
    }
    objectDetailsPromise = null;
  }
}

export async function getObjectDetails(
  objectName: string,
  unityConnection: UnityConnection
): Promise<{ content: { type: "text"; text: string }[] }> {
  try {
    unityConnection.sendMessage("getGameObjectDetails", {
      objectName: objectName,
    });

    // Wait for result with timeout
    const timeoutMs = 30000;
    const details = await Promise.race([
      new Promise<ObjectDetails>((resolve, reject) => {
        objectDetailsPromise = { resolve, reject };
      }),
      new Promise<never>((_, reject) =>
        setTimeout(
          () =>
            reject(
              new Error(
                `Getting object details timed out after ${
                  timeoutMs / 1000
                } seconds.`
              )
            ),
          timeoutMs
        )
      ),
    ]);

    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify(details, null, 2),
        },
      ],
    };
  } catch (error) {
    const errorMessage = error instanceof Error ? error.message : "Unknown error";
    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify({
            error: `Failed to get object details: ${errorMessage}`,
            status: "error",
          }),
        },
      ],
    };
  }
}
