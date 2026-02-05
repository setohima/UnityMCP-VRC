import { LogEntry } from "./types.js";

interface GetLogsArgs {
  types?: ("Log" | "Warning" | "Error" | "Exception")[];
  count?: number;
  fields?: ("message" | "stackTrace" | "logType" | "timestamp")[];
  messageContains?: string;
  stackTraceContains?: string;
  timestampAfter?: string;
  timestampBefore?: string;
}

/**
 * Retrieve and filter Unity Editor logs.
 */
export function getLogs(
  args: GetLogsArgs,
  logBuffer: LogEntry[]
): { content: { type: string; text: string }[] } {
  const options = {
    types: args?.types,
    count: args?.count || 100,
    fields: args?.fields,
    messageContains: args?.messageContains,
    stackTraceContains: args?.stackTraceContains,
    timestampAfter: args?.timestampAfter,
    timestampBefore: args?.timestampBefore,
  };

  const logs = filterLogs(logBuffer, options);
  return {
    content: [
      {
        type: "text",
        text: JSON.stringify(logs, null, 2),
      },
    ],
  };
}

function filterLogs(
  logBuffer: LogEntry[],
  options: {
    types?: string[];
    count?: number;
    fields?: string[];
    messageContains?: string;
    stackTraceContains?: string;
    timestampAfter?: string;
    timestampBefore?: string;
  }
): any[] {
  const {
    types,
    count = 100,
    fields,
    messageContains,
    stackTraceContains,
    timestampAfter,
    timestampBefore,
  } = options;

  // First apply all filters
  let filteredLogs = logBuffer.filter((log) => {
    // Type filter
    if (types && !types.includes(log.logType)) return false;

    // Message content filter
    if (messageContains && !log.message.includes(messageContains))
      return false;

    // Stack trace content filter
    if (stackTraceContains && !log.stackTrace.includes(stackTraceContains))
      return false;

    // Timestamp filters
    if (timestampAfter && new Date(log.timestamp) < new Date(timestampAfter))
      return false;
    if (
      timestampBefore &&
      new Date(log.timestamp) > new Date(timestampBefore)
    )
      return false;

    return true;
  });

  // Then apply count limit
  filteredLogs = filteredLogs.slice(-count);

  // Finally apply field selection if specified
  if (fields?.length) {
    return filteredLogs.map((log) => {
      const selectedFields: Partial<LogEntry> = {};
      fields.forEach((field) => {
        if (
          field in log &&
          (field === "message" ||
            field === "stackTrace" ||
            field === "logType" ||
            field === "timestamp")
        ) {
          selectedFields[field as keyof LogEntry] =
            log[field as keyof LogEntry];
        }
      });
      return selectedFields;
    });
  }

  return filteredLogs;
}
