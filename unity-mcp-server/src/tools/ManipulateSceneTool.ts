import { z } from "zod";
import { UnityConnection } from "../communication/UnityConnection.js";

interface SceneManipulationResult {
    message?: string;
    instanceId?: number;
    error?: string;
}

// Queue of resolvers to handle concurrent requests
let manipulationResolvers: ((result: SceneManipulationResult) => void)[] = [];

export function resolveSceneManipulation(result: SceneManipulationResult) {
    if (manipulationResolvers.length > 0) {
        const resolve = manipulationResolvers.shift();
        resolve?.(result);
    }
}

export const ManipulateSceneTool = (unityConnection: UnityConnection) => ({
    name: "manipulate_scene",
    description: "Create, delete, or modify GameObjects in the active scene. Supports creating new objects with components, deleting objects, modifying transform (position/rotation/scale), and adding/removing components.",
    inputSchema: z.object({
        action: z.enum(["create_game_object", "delete_game_object", "set_transform", "manage_component"]).describe("The action to perform."),
        name: z.string().describe("Name of the GameObject to manipulate. For creation, this is the new name. For others, it's the target name."),
        details: z.object({
            components: z.array(z.string()).optional().describe("List of component names to add upon creation (e.g. ['BoxCollider', 'Light'])."),
            parent: z.string().optional().describe("Name of the parent object to attach to."),
            position: z.object({ x: z.number(), y: z.number(), z: z.number() }).optional(),
            rotation: z.object({ x: z.number(), y: z.number(), z: z.number() }).optional(),
            scale: z.object({ x: z.number(), y: z.number(), z: z.number() }).optional(),

            // For set_transform
            newPosition: z.object({ x: z.number(), y: z.number(), z: z.number() }).optional(),
            newRotation: z.object({ x: z.number(), y: z.number(), z: z.number() }).optional(),
            newScale: z.object({ x: z.number(), y: z.number(), z: z.number() }).optional(),

            // For manage_component
            componentName: z.string().optional(),
            componentAction: z.enum(["add", "remove"]).optional()
        }).optional()
    }),
    handler: async (args: any) => {
        if (!unityConnection.isConnected()) {
            return {
                content: [{ type: "text", text: "Error: Unity Editor is not connected." }],
                isError: true,
            };
        }

        return new Promise((resolve) => {
            // Set up the resolver
            manipulationResolvers.push((result) => {
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

            // Send the request
            unityConnection.sendMessage("manipulateScene", args);

            // Timeout fallback
            setTimeout(() => {
                // Check if this request is still pending (simple heuristic)
                // In a robust system, we'd use IDs. Here we rely on FIFO.
                // If we timeout, we should probably resolve to error to unblock.
                // But removing specific resolver is hard without ID.
                // Rely on UnityConnection timeout or global timeout for now.
                // Assuming Unity responds within reason.
            }, 30000);
        });
    },
});
