import { UnityConnection } from "../communication/UnityConnection.js";

// Request/Response types matching C# implementation
interface ScreenshotResult {
    base64: string;
    format: string;
    error?: string;
}

interface ScreenshotHandler {
    resolve: (value: ScreenshotResult) => void;
    reject: (reason?: any) => void;
}

// Queue for pending requests
let screenshotPromise: ScreenshotHandler | null = null;

export function resolveScreenshot(result: any): void {
    if (screenshotPromise) {
        if (result.error) {
            screenshotPromise.reject(new Error(result.error));
        } else {
            screenshotPromise.resolve(result);
        }
        screenshotPromise = null;
    }
}

export async function takeScreenshot(
    unityConnection: UnityConnection
): Promise<{ content: ({ type: "text"; text: string } | { type: "image"; data: string; mimeType: string })[] }> {
    try {
        unityConnection.sendMessage("takeScreenshot", {});

        // Wait for result with timeout
        const timeoutMs = 30000;
        const result = await Promise.race([
            new Promise<ScreenshotResult>((resolve, reject) => {
                screenshotPromise = { resolve, reject };
            }),
            new Promise<never>((_, reject) =>
                setTimeout(
                    () =>
                        reject(
                            new Error(
                                `Screenshot capture timed out after ${timeoutMs / 1000} seconds.`
                            )
                        ),
                    timeoutMs
                )
            ),
        ]);

        return {
            content: [
                {
                    type: "image" as const,
                    data: result.base64,
                    mimeType: "image/jpeg",
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
                        error: `Failed to take screenshot: ${errorMessage}`,
                        status: "error",
                    }),
                },
            ],
        };
    }
}
