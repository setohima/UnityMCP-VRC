using UnityEngine;
using UnityEditor;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;

namespace UnityMCP.Editor
{
    public class AssetManager
    {
        public class ManageAssetsData
        {
            public string action { get; set; } // "search", "refresh"
            public string filter { get; set; } // For search (e.g. "t:Material")
        }

        public async Task HandleAssetManagement(ClientWebSocket webSocket, CancellationToken cancellationToken, string dataJson)
        {
            try
            {
                var requestData = JsonConvert.DeserializeObject<ManageAssetsData>(dataJson);
                object result = null;

                await EditorUtilities.WaitForUnityCompilationAsync();

                // Dispatch to main thread
                var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        tcs.SetResult(ProcessAction(requestData));
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                };

                result = await tcs.Task;

                var message = JsonConvert.SerializeObject(new
                {
                    type = "assetManagementResult",
                    data = result
                });

                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
                Debug.Log($"[UnityMCP] Asset management '{requestData.action}' completed.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Error managing assets: {e.Message}");
                var errorMessage = JsonConvert.SerializeObject(new
                {
                    type = "assetManagementResult",
                    data = new { error = e.Message }
                });
                var buffer = Encoding.UTF8.GetBytes(errorMessage);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
            }
        }

        private object ProcessAction(ManageAssetsData data)
        {
            switch (data.action)
            {
                case "search":
                    return SearchAssets(data.filter);
                case "refresh":
                    AssetDatabase.Refresh();
                    return new { message = "AssetDatabase refreshed" };
                default:
                    throw new Exception($"Unknown action: {data.action}");
            }
        }

        private object SearchAssets(string filter)
        {
            string[] guids = AssetDatabase.FindAssets(filter);
            var results = new List<object>();

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                results.Add(new { guid = guid, path = path });
            }

            return new { count = results.Count, results = results };
        }
    }
}
