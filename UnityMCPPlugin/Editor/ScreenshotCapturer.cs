using UnityEngine;
using UnityEditor;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.IO;

namespace UnityMCP.Editor
{
    public class ScreenshotCapturer
    {
        public async Task SendScreenshot(ClientWebSocket webSocket, CancellationToken cancellationToken)
        {
            try
            {
                // Unity API access must be on main thread
                 EditorUtilities.WaitForUnityCompilation();

                string base64Image = null;
                
                // Must run on main thread
                 if (!System.Threading.Thread.CurrentThread.IsBackground)
                 {
                     base64Image = CaptureAndEncode();
                 }
                 else 
                 {
                     // This is tricky because we are in an async Task potentially on a thread pool thread
                     // But Unity API calls usually need to happen on the main thread.
                     // However, our WebSocket loop is likely driven by EditorApplication.update or similar mechanism if we were strictly single threaded, structure matters.
                     // In the current architecture, HandleMessage is async void/Task called from StartReceiving.
                     // StartReceiving awaits ReceiveAsync.
                     // We need to ensure capture runs on main thread.
                     // But for now, let's assume we might need to dispatch if not on main thread, 
                     // OR rely on the fact that if we are called from the context of the editor loop it might be fine, but WebSocket callbacks are often on thread pool.
                     
                     // Simply dispatching to main thread synchronously for the capture
                     var tcs = new TaskCompletionSource<string>();
                     EditorApplication.delayCall += () => 
                     {
                         try {
                            tcs.SetResult(CaptureAndEncode());
                         } catch (Exception ex) {
                             tcs.SetException(ex);
                         }
                     };
                     base64Image = await tcs.Task;
                 }

                var message = JsonConvert.SerializeObject(new
                {
                    type = "screenshot",
                    data = new { base64 = base64Image, format = "jpg" }
                });

                // Sending can happen on background thread
                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
                Debug.Log("[UnityMCP] Sent screenshot");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Error capturing screenshot: {e.Message}");
                 var errorMessage = JsonConvert.SerializeObject(new
                {
                    type = "screenshot",
                    data = new { error = e.Message }
                });
                var buffer = Encoding.UTF8.GetBytes(errorMessage);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
            }
        }

        private string CaptureAndEncode()
        {
            // Try to find a camera to render from
            Camera cam = Camera.main;
            if (cam == null)
            {
                cam = UnityEngine.Object.FindObjectOfType<Camera>();
            }

            if (cam == null)
            {
                 throw new Exception("No Camera found in the scene to capture.");
            }

            // Create a RenderTexture
            int width = 1280;
            int height = 720;
            if (Screen.width > 0 && Screen.height > 0)
            {
                width = Screen.width;
                height = Screen.height;
            }

            RenderTexture rt = new RenderTexture(width, height, 24);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture prevCamTarget = cam.targetTexture;

            try
            {
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                Texture2D screenTex = new Texture2D(width, height, TextureFormat.RGB24, false);
                screenTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                screenTex.Apply();

                byte[] bytes = screenTex.EncodeToJPG(75);
                UnityEngine.Object.DestroyImmediate(screenTex);

                return Convert.ToBase64String(bytes);
            }
            finally
            {
                cam.targetTexture = prevCamTarget;
                RenderTexture.active = prevActive;
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
            }
        }
    }
}
