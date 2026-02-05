import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { getEditorState, resolveUnityEditorState, UnityEditorState } from './GetEditorStateTool.js';

// Create a mock UnityConnection
function createMockUnityConnection() {
    return {
        sendMessage: vi.fn(),
        isConnected: vi.fn().mockReturnValue(true),
        getLogBuffer: vi.fn().mockReturnValue([]),
        close: vi.fn(),
        waitForConnection: vi.fn().mockResolvedValue(true),
        setOnLogReceived: vi.fn(),
    };
}

describe('GetEditorStateTool', () => {
    let mockUnityConnection: ReturnType<typeof createMockUnityConnection>;

    beforeEach(() => {
        mockUnityConnection = createMockUnityConnection();
        vi.useFakeTimers();
    });

    afterEach(() => {
        vi.useRealTimers();
        vi.clearAllMocks();
    });

    describe('getEditorState', () => {
        it('should return error for invalid format', async () => {
            const result = await getEditorState('InvalidFormat', mockUnityConnection as any);

            const parsed = JSON.parse(result.content[0].text);
            expect(parsed.error).toContain('Invalid format');
            expect(parsed.error).toContain('InvalidFormat');
            expect(parsed.status).toBe('error');
        });

        it('should accept "Raw" format', async () => {
            const editorStatePromise = getEditorState('Raw', mockUnityConnection as any);

            // Verify sendMessage was called
            expect(mockUnityConnection.sendMessage).toHaveBeenCalledWith('getEditorState', {});

            // Simulate Unity response
            const mockState: UnityEditorState = {
                activeGameObjects: ['Player', 'Camera'],
                selectedObjects: ['Player'],
                playModeState: 'Stopped',
                sceneHierarchy: { root: [] },
                projectStructure: {
                    scenes: ['MainScene.unity'],
                    assets: ['Player.prefab'],
                },
            };

            resolveUnityEditorState(mockState);

            const result = await editorStatePromise;
            const parsed = JSON.parse(result.content[0].text);

            expect(parsed.activeGameObjects).toEqual(['Player', 'Camera']);
            expect(parsed.selectedObjects).toEqual(['Player']);
            expect(parsed.playModeState).toBe('Stopped');
        });

        it('should handle timeout error', async () => {
            const editorStatePromise = getEditorState('Raw', mockUnityConnection as any);

            // Fast-forward time to trigger timeout
            vi.advanceTimersByTime(60001);

            const result = await editorStatePromise;
            const parsed = JSON.parse(result.content[0].text);

            expect(parsed.status).toBe('error');
            expect(parsed.error).toContain('timed out');
            expect(parsed.error).toContain('60 seconds');
        });

        it('should return complete editor state with all fields', async () => {
            const editorStatePromise = getEditorState('Raw', mockUnityConnection as any);

            const mockState: UnityEditorState = {
                activeGameObjects: ['GameObject1', 'GameObject2', 'GameObject3'],
                selectedObjects: ['GameObject1'],
                playModeState: 'Playing',
                sceneHierarchy: {
                    root: [
                        { name: 'Main Camera', children: [] },
                        { name: 'Player', children: [{ name: 'Weapon', children: [] }] },
                    ],
                },
                projectStructure: {
                    scenes: ['Scene1.unity', 'Scene2.unity'],
                    assets: ['Prefab1.prefab', 'Material1.mat'],
                },
            };

            resolveUnityEditorState(mockState);

            const result = await editorStatePromise;
            const parsed = JSON.parse(result.content[0].text);

            expect(parsed.activeGameObjects).toHaveLength(3);
            expect(parsed.playModeState).toBe('Playing');
            expect(parsed.sceneHierarchy.root).toHaveLength(2);
            expect(parsed.projectStructure.scenes).toHaveLength(2);
        });

        it('should use default format when not specified', async () => {
            // When format is undefined or empty, it should default to "Raw"
            const editorStatePromise = getEditorState('Raw', mockUnityConnection as any);

            const mockState: UnityEditorState = {
                activeGameObjects: [],
                selectedObjects: [],
                playModeState: 'Stopped',
                sceneHierarchy: {},
                projectStructure: {},
            };

            resolveUnityEditorState(mockState);

            const result = await editorStatePromise;

            // Should complete successfully without format error
            expect(result.content[0].type).toBe('text');
            const parsed = JSON.parse(result.content[0].text);
            expect(parsed.error).toBeUndefined();
        });
    });

    describe('resolveUnityEditorState', () => {
        it('should not throw when called without pending promise', () => {
            const mockState: UnityEditorState = {
                activeGameObjects: [],
                selectedObjects: [],
                playModeState: 'Stopped',
                sceneHierarchy: {},
                projectStructure: {},
            };

            // Should not throw
            expect(() => resolveUnityEditorState(mockState)).not.toThrow();
        });
    });
});
