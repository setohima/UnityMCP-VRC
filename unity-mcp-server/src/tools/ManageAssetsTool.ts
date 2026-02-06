import { z } from "zod";
import { UnityConnection } from "../communication/UnityConnection.js";

interface AssetManagementResult {
    message?: string;
    count?: number;
    results?: any[];
    error?: string;
}

let assetResolvers: ((result: AssetManagementResult) => void)[] = [];

export function resolveAssetManagement(result: AssetManagementResult) {
    if (assetResolvers.length > 0) {
        const resolve = assetResolvers.shift();
        resolve?.(result);
    }
}

export const ManageAssetsTool = (unityConnection: UnityConnection) => ({
    name: "manage_assets",
    description: "Search for assets or refresh the AssetDatabase.",
    inputSchema: z.object({
        action: z.enum(["search", "refresh"]).describe("The action to perform."),
        filter: z.string().optional().describe("Filter string for search (e.g., 't:Material', 'MyScript'). Required if action is 'search'.")
    }),
    handler: async (args: any) => {
        if (!unityConnection.isConnected()) {
            return {
                content: [{ type: "text", text: "Error: Unity Editor is not connected." }],
                isError: true,
            };
        }

        if (args.action === "search" && !args.filter) {
            return {
                content: [{ type: "text", text: "Error: 'filter' is required for search action." }],
                isError: true,
            };
        }

        return new Promise((resolve) => {
            assetResolvers.push((result) => {
                if (result.error) {
                    resolve({
                        content: [{ type: "text", text: `Error: ${result.error}` }],
                        isError: true,
                    });
                } else {
                    resolve({
                        content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
                    });
                }
            });

            unityConnection.sendMessage("manageAssets", args);
        });
    },
});
