import { describe, it, expect } from 'vitest';
import { getLogs } from './GetLogsTool.js';
import { LogEntry } from './types.js';

// Helper function to create test log entries
function createLogEntry(
    overrides: Partial<LogEntry> = {}
): LogEntry {
    return {
        message: 'Test log message',
        stackTrace: 'at TestClass.Method() in Test.cs:line 10',
        logType: 'Log',
        timestamp: '2024-01-15T10:00:00.000Z',
        ...overrides,
    };
}

describe('GetLogsTool', () => {
    describe('getLogs', () => {
        it('should return all logs when no filters are applied', () => {
            const logBuffer: LogEntry[] = [
                createLogEntry({ message: 'Log 1' }),
                createLogEntry({ message: 'Log 2' }),
                createLogEntry({ message: 'Log 3' }),
            ];

            const result = getLogs({}, logBuffer);

            expect(result.content).toHaveLength(1);
            expect(result.content[0].type).toBe('text');

            const parsedLogs = JSON.parse(result.content[0].text);
            expect(parsedLogs).toHaveLength(3);
        });

        it('should filter logs by type', () => {
            const logBuffer: LogEntry[] = [
                createLogEntry({ logType: 'Log', message: 'Info message' }),
                createLogEntry({ logType: 'Warning', message: 'Warning message' }),
                createLogEntry({ logType: 'Error', message: 'Error message' }),
                createLogEntry({ logType: 'Exception', message: 'Exception message' }),
            ];

            const result = getLogs({ types: ['Error', 'Exception'] }, logBuffer);
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(2);
            expect(parsedLogs[0].logType).toBe('Error');
            expect(parsedLogs[1].logType).toBe('Exception');
        });

        it('should limit logs by count', () => {
            const logBuffer: LogEntry[] = Array.from({ length: 10 }, (_, i) =>
                createLogEntry({ message: `Log ${i + 1}` })
            );

            const result = getLogs({ count: 3 }, logBuffer);
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(3);
            // Should return the last 3 logs (most recent)
            expect(parsedLogs[0].message).toBe('Log 8');
            expect(parsedLogs[1].message).toBe('Log 9');
            expect(parsedLogs[2].message).toBe('Log 10');
        });

        it('should filter logs by messageContains', () => {
            const logBuffer: LogEntry[] = [
                createLogEntry({ message: 'Player connected' }),
                createLogEntry({ message: 'Enemy spawned' }),
                createLogEntry({ message: 'Player disconnected' }),
            ];

            const result = getLogs({ messageContains: 'Player' }, logBuffer);
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(2);
            expect(parsedLogs[0].message).toBe('Player connected');
            expect(parsedLogs[1].message).toBe('Player disconnected');
        });

        it('should filter logs by stackTraceContains', () => {
            const logBuffer: LogEntry[] = [
                createLogEntry({ stackTrace: 'at PlayerController.Update()' }),
                createLogEntry({ stackTrace: 'at EnemyAI.Think()' }),
                createLogEntry({ stackTrace: 'at PlayerController.Move()' }),
            ];

            const result = getLogs({ stackTraceContains: 'PlayerController' }, logBuffer);
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(2);
        });

        it('should filter logs by timestamp range', () => {
            const logBuffer: LogEntry[] = [
                createLogEntry({ timestamp: '2024-01-15T08:00:00.000Z' }),
                createLogEntry({ timestamp: '2024-01-15T10:00:00.000Z' }),
                createLogEntry({ timestamp: '2024-01-15T12:00:00.000Z' }),
                createLogEntry({ timestamp: '2024-01-15T14:00:00.000Z' }),
            ];

            const result = getLogs(
                {
                    timestampAfter: '2024-01-15T09:00:00.000Z',
                    timestampBefore: '2024-01-15T13:00:00.000Z',
                },
                logBuffer
            );
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(2);
        });

        it('should select specific fields when fields option is provided', () => {
            const logBuffer: LogEntry[] = [
                createLogEntry({
                    message: 'Test message',
                    logType: 'Warning',
                    stackTrace: 'stack trace here',
                    timestamp: '2024-01-15T10:00:00.000Z',
                }),
            ];

            const result = getLogs({ fields: ['message', 'logType'] }, logBuffer);
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(1);
            expect(parsedLogs[0]).toHaveProperty('message');
            expect(parsedLogs[0]).toHaveProperty('logType');
            expect(parsedLogs[0]).not.toHaveProperty('stackTrace');
            expect(parsedLogs[0]).not.toHaveProperty('timestamp');
        });

        it('should combine multiple filters', () => {
            const logBuffer: LogEntry[] = [
                createLogEntry({ logType: 'Error', message: 'Player error at start' }),
                createLogEntry({ logType: 'Warning', message: 'Player warning' }),
                createLogEntry({ logType: 'Error', message: 'Enemy error' }),
                createLogEntry({ logType: 'Error', message: 'Player error at end' }),
            ];

            const result = getLogs(
                {
                    types: ['Error'],
                    messageContains: 'Player',
                },
                logBuffer
            );
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(2);
            expect(parsedLogs[0].message).toBe('Player error at start');
            expect(parsedLogs[1].message).toBe('Player error at end');
        });

        it('should return empty array when no logs match', () => {
            const logBuffer: LogEntry[] = [
                createLogEntry({ logType: 'Log', message: 'Normal log' }),
            ];

            const result = getLogs({ types: ['Error'] }, logBuffer);
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(0);
        });

        it('should handle empty log buffer', () => {
            const result = getLogs({}, []);
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(0);
        });

        it('should default count to 100', () => {
            const logBuffer: LogEntry[] = Array.from({ length: 150 }, (_, i) =>
                createLogEntry({ message: `Log ${i + 1}` })
            );

            const result = getLogs({}, logBuffer);
            const parsedLogs = JSON.parse(result.content[0].text);

            expect(parsedLogs).toHaveLength(100);
            // Should return the last 100 logs
            expect(parsedLogs[0].message).toBe('Log 51');
            expect(parsedLogs[99].message).toBe('Log 150');
        });
    });
});
