using UnityEngine;
using UnityEditor;
using System;
using System.Threading.Tasks;

namespace UnityMCP.Editor
{
    public static class EditorUtilities
    {
        /// <summary>
        /// Waits for Unity to finish any ongoing compilation or asset processing
        /// </summary>
        /// <param name="timeoutSeconds">Maximum time to wait in seconds (0 means no timeout)</param>
        /// <returns>True if compilation finished, false if timed out</returns>
        public static async Task WaitForUnityCompilationAsync(float timeoutSeconds = 10f)
        {
            if (!EditorApplication.isCompiling)
                return;

            Debug.Log("[UnityMCP] Waiting for Unity to finish compilation...");

            float startTime = Time.realtimeSinceStartup;
            
            while (EditorApplication.isCompiling)
            {
                if (timeoutSeconds > 0 && (Time.realtimeSinceStartup - startTime) > timeoutSeconds)
                {
                    Debug.LogWarning($"[UnityMCP] Timed out waiting for Unity compilation after {timeoutSeconds} seconds");
                    return;
                }
                
                await Task.Delay(100);
            }

            Debug.Log("[UnityMCP] Unity compilation completed");
            
            // Force a small delay to ensure any final processing is complete
            await Task.Delay(500);
        }

        public static void WaitForUnityCompilation(float timeoutSeconds = 60f)
        {
            if (EditorApplication.isCompiling)
            {
                Debug.LogWarning("[UnityMCP] Unity is compiling. Synchronous wait requested but skipped to avoid deadlock.");
            }
        }
    }
}
