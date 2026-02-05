import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { executeEditorCommand, resolveCommandResult, CommandResult } from './ExecuteEditorCommandTool.js';

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

describe('ExecuteEditorCommandTool', () => {
    let mockUnityConnection: ReturnType<typeof createMockUnityConnection>;

    beforeEach(() => {
        mockUnityConnection = createMockUnityConnection();
        vi.useFakeTimers();
    });

    afterEach(() => {
        vi.useRealTimers();
        vi.clearAllMocks();
    });

    describe('executeEditorCommand', () => {
        it('should return error for empty code', async () => {
            const result = await executeEditorCommand('', mockUnityConnection as any);

            expect(result.content).toHaveLength(1);
            const parsed = JSON.parse(result.content[0].text);
            expect(parsed.error).toBe('The code parameter is required and cannot be empty');
            expect(parsed.status).toBe('error');
        });

        it('should return error for whitespace-only code', async () => {
            const result = await executeEditorCommand('   ', mockUnityConnection as any);

            const parsed = JSON.parse(result.content[0].text);
            expect(parsed.error).toBe('The code parameter is required and cannot be empty');
            expect(parsed.status).toBe('error');
        });

        it('should return error for null code', async () => {
            const result = await executeEditorCommand(null as any, mockUnityConnection as any);

            const parsed = JSON.parse(result.content[0].text);
            expect(parsed.error).toBe('The code parameter is required and cannot be empty');
            expect(parsed.status).toBe('error');
        });

        it('should send command to Unity and return result on success', async () => {
            const code = `
        using UnityEngine;
        public class EditorCommand {
          public static object Execute() {
            return "Hello World";
          }
        }
      `;

            const commandPromise = executeEditorCommand(code, mockUnityConnection as any);

            // Verify sendMessage was called
            expect(mockUnityConnection.sendMessage).toHaveBeenCalledWith('executeEditorCommand', {
                code: code,
            });

            // Simulate Unity response
            const mockResult: CommandResult = {
                result: 'Hello World',
                logs: [],
                errors: [],
                warnings: [],
                executionSuccess: true,
            };

            // Resolve the command immediately
            resolveCommandResult(mockResult);

            const result = await commandPromise;
            const parsed = JSON.parse(result.content[0].text);

            expect(parsed.status).toBe('success');
            expect(parsed.result).toEqual(mockResult);
            expect(parsed.executionTime).toBeDefined();
        });

        it('should handle timeout error', async () => {
            const code = 'public class EditorCommand { public static object Execute() { return null; } }';

            const commandPromise = executeEditorCommand(code, mockUnityConnection as any);

            // Fast-forward time to trigger timeout
            vi.advanceTimersByTime(60001);

            const result = await commandPromise;
            const parsed = JSON.parse(result.content[0].text);

            expect(parsed.status).toBe('error');
            expect(parsed.error).toContain('timed out');
            expect(parsed.error).toContain('60 seconds');
        });

        it('should return command result with logs and warnings', async () => {
            const code = 'public class EditorCommand { public static object Execute() { return 42; } }';

            const commandPromise = executeEditorCommand(code, mockUnityConnection as any);

            const mockResult: CommandResult = {
                result: 42,
                logs: ['Starting execution', 'Completed'],
                errors: [],
                warnings: ['Performance warning'],
                executionSuccess: true,
            };

            resolveCommandResult(mockResult);

            const result = await commandPromise;
            const parsed = JSON.parse(result.content[0].text);

            expect(parsed.status).toBe('success');
            expect(parsed.result.logs).toEqual(['Starting execution', 'Completed']);
            expect(parsed.result.warnings).toEqual(['Performance warning']);
        });
    });

    describe('resolveCommandResult', () => {
        it('should not throw when called without pending promise', () => {
            const mockResult: CommandResult = {
                result: 'test',
                logs: [],
                errors: [],
                warnings: [],
                executionSuccess: true,
            };

            // Should not throw
            expect(() => resolveCommandResult(mockResult)).not.toThrow();
        });
    });
});
