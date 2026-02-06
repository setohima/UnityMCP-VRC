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
    public class SceneManipulator
    {
        public class ManipulateSceneData
        {
            public string action { get; set; }
            public string name { get; set; } // Name or Path
            public SceneManipulationDetails details { get; set; }
        }

        public class SceneManipulationDetails
        {
            // For create
            public string[] components { get; set; }
            public Vector3Data position { get; set; }
            public Vector3Data rotation { get; set; }
            public Vector3Data scale { get; set; }
            public string parent { get; set; }

            // For set_transform
            public Vector3Data newPosition { get; set; }
            public Vector3Data newRotation { get; set; }
            public Vector3Data newScale { get; set; }

            // For manage_component
            public string componentName { get; set; }
            public string componentAction { get; set; } // "add" or "remove"
        }

        public class Vector3Data
        {
            public float x { get; set; }
            public float y { get; set; }
            public float z { get; set; }

            public Vector3 ToVector3() => new Vector3(x, y, z);
        }

        public async Task HandleSceneManipulation(ClientWebSocket webSocket, CancellationToken cancellationToken, string dataJson)
        {
            try
            {
                var requestData = JsonConvert.DeserializeObject<ManipulateSceneData>(dataJson);
                object result = null;

                await EditorUtilities.WaitForUnityCompilationAsync();

                // Dispatch to main thread
                var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        Debug.Log($"[UnityMCP] Processing scene manipulation: {requestData.action}");
                        var processedResult = ProcessAction(requestData);
                        Debug.Log("[UnityMCP] Scene manipulation processed, setting result");
                        tcs.SetResult(processedResult);
                        Debug.Log("[UnityMCP] Result set on TCS");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[UnityMCP] Error manipulating scene: {ex.Message}");
                        tcs.SetResult(new { error = ex.Message });
                    }
                };

                Debug.Log("[UnityMCP] Waiting for TCS task...");
                result = await tcs.Task;
                Debug.Log("[UnityMCP] TCS task completed");

                var message = JsonConvert.SerializeObject(new
                {
                    type = "sceneManipulationResult",
                    data = result
                });

                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
                Debug.Log($"[UnityMCP] Scene manipulation '{requestData.action}' completed.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Error manipulating scene: {e.Message}");
                var errorMessage = JsonConvert.SerializeObject(new
                {
                    type = "sceneManipulationResult",
                    data = new { error = e.Message }
                });
                var buffer = Encoding.UTF8.GetBytes(errorMessage);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
            }
        }

        private object ProcessAction(ManipulateSceneData data)
        {
            switch (data.action)
            {
                case "create_game_object":
                    return CreateGameObject(data);
                case "delete_game_object":
                    return DeleteGameObject(data.name);
                case "set_transform":
                    return SetTransform(data);
                case "manage_component":
                    return ManageComponent(data);
                default:
                    throw new Exception($"Unknown action: {data.action}");
            }
        }

        private object CreateGameObject(ManipulateSceneData data)
        {
            GameObject obj = new GameObject(data.name);
            
            if (!string.IsNullOrEmpty(data.details?.parent))
            {
                var parentObj = GameObject.Find(data.details.parent);
                if (parentObj != null)
                {
                    obj.transform.SetParent(parentObj.transform, false);
                }
            }

            if (data.details?.position != null) obj.transform.position = data.details.position.ToVector3();
            if (data.details?.rotation != null) obj.transform.rotation = Quaternion.Euler(data.details.rotation.ToVector3());
            if (data.details?.scale != null) obj.transform.localScale = data.details.scale.ToVector3();

            if (data.details?.components != null)
            {
                foreach (var compName in data.details.components)
                {
                    AddComponentByName(obj, compName);
                }
            }

            // Register undo
            Undo.RegisterCreatedObjectUndo(obj, "Create GameObject (MCP)");

            return new { message = $"Created GameObject '{obj.name}'", instanceId = obj.GetInstanceID() };
        }

        private object DeleteGameObject(string name)
        {
            var obj = GameObject.Find(name);
            if (obj == null) throw new Exception($"GameObject '{name}' not found");

            Undo.DestroyObjectImmediate(obj);
            return new { message = $"Deleted GameObject '{name}'" };
        }

        private object SetTransform(ManipulateSceneData data)
        {
            var obj = GameObject.Find(data.name);
            if (obj == null) throw new Exception($"GameObject '{data.name}' not found");

            Undo.RecordObject(obj.transform, "Set Transform (MCP)");

            if (data.details?.newPosition != null) obj.transform.position = data.details.newPosition.ToVector3();
            if (data.details?.newRotation != null) obj.transform.rotation = Quaternion.Euler(data.details.newRotation.ToVector3());
            if (data.details?.newScale != null) obj.transform.localScale = data.details.newScale.ToVector3();

            return new { message = $"Updated transform for '{obj.name}'" };
        }

        private object ManageComponent(ManipulateSceneData data)
        {
            var obj = GameObject.Find(data.name);
            if (obj == null) throw new Exception($"GameObject '{data.name}' not found");

            string compName = data.details?.componentName;
            if (string.IsNullOrEmpty(compName)) throw new Exception("Component name required");

            if (data.details.componentAction == "add")
            {
                var comp = AddComponentByName(obj, compName);
                Undo.RegisterCreatedObjectUndo(comp.gameObject, "Add Component (MCP)"); // Note: Undo for add component is tricky, often recorded on GO
                return new { message = $"Added component '{compName}' to '{obj.name}'" };
            }
            else if (data.details.componentAction == "remove")
            {
                var comp = obj.GetComponent(compName);
                if (comp == null) throw new Exception($"Component '{compName}' not found on '{obj.name}'");
                Undo.DestroyObjectImmediate(comp);
                return new { message = $"Removed component '{compName}' from '{obj.name}'" };
            }
            
            throw new Exception("Invalid component action");
        }

        private Component AddComponentByName(GameObject obj, string className)
        {
            // Try fundamental types first
            switch (className)
            {
                case "BoxCollider": return obj.AddComponent<BoxCollider>();
                case "SphereCollider": return obj.AddComponent<SphereCollider>();
                case "Rigidbody":
                case "RigidBody": return obj.AddComponent<Rigidbody>();
                case "Light": return obj.AddComponent<Light>();
                case "Camera": return obj.AddComponent<Camera>();
            }

            // Try reflection
            System.Type type = System.Type.GetType(className + ", UnityEngine");
            if (type == null)
            {
                // Try to find in assemblies
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType(className);
                    if (type != null) break;
                }
            }

            if (type != null && typeof(Component).IsAssignableFrom(type))
            {
                return obj.AddComponent(type);
            }

            throw new Exception($"Could not find Component type '{className}'");
        }
    }
}
