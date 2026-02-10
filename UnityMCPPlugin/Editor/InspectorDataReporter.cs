using UnityEngine;
using UnityEditor;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Reflection;

namespace UnityMCP.Editor
{
    public class InspectorDataReporter
    {
        public class GetObjectDetailsData
        {
            public string objectName { get; set; }
        }

        public async Task SendObjectDetails(ClientWebSocket webSocket, CancellationToken cancellationToken, string dataJson)
        {
            try
            {
                var requestData = JsonConvert.DeserializeObject<GetObjectDetailsData>(dataJson);

                // Wait for any ongoing compilation
                await EditorUtilities.WaitForUnityCompilationAsync();

                // Dispatch to main thread using delayCall
                var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        var details = GetObjectDetails(requestData.objectName);
                        tcs.SetResult(details);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                };

                var result = await tcs.Task;

                var message = JsonConvert.SerializeObject(new
                {
                    type = "objectDetails",
                    data = result
                });

                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
                Debug.Log($"[UnityMCP] Sent details for object: {requestData.objectName}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Error getting object details: {e.Message}");
                // Send error response
                 var errorMessage = JsonConvert.SerializeObject(new
                {
                    type = "objectDetails",
                    data = new { error = e.Message }
                });
                var buffer = Encoding.UTF8.GetBytes(errorMessage);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
            }
        }

        private object GetObjectDetails(string objectName)
        {
            var obj = GameObject.Find(objectName);
            if (obj == null)
            {
                return new { error = $"GameObject '{objectName}' not found." };
            }

            var componentsData = new List<object>();
            var components = obj.GetComponents<Component>();

            foreach (var comp in components)
            {
                if (comp == null) continue;

                var compType = comp.GetType();
                var fieldsData = new Dictionary<string, object>();

                // Get public fields
                foreach (var field in compType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (IsSerializableType(field.FieldType))
                    {
                        try {
                            fieldsData[field.Name] = FormatValue(field.GetValue(comp));
                        } catch {}
                    }
                }

                // Get public properties (that are readable and not obsolete)
                foreach (var prop in compType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.CanRead && IsSerializableType(prop.PropertyType) && prop.GetIndexParameters().Length == 0)
                    {
                         try {
                             // Skip some dangerous or heavy properties
                             if (prop.Name == "mesh" || prop.Name == "material" || prop.Name == "materials") continue; 
                             
                            fieldsData[prop.Name] = FormatValue(prop.GetValue(comp));
                        } catch {}
                    }
                }

                componentsData.Add(new
                {
                    type = compType.Name,
                    data = fieldsData
                });
            }

            return new
            {
                name = obj.name,
                active = obj.activeSelf,
                tag = obj.tag,
                layer = LayerMask.LayerToName(obj.layer),
                transform = new {
                    position = FormatValue(obj.transform.position),
                    rotation = FormatValue(obj.transform.rotation.eulerAngles),
                    scale = FormatValue(obj.transform.localScale)
                },
                components = componentsData
            };
        }

        private bool IsSerializableType(Type type)
        {
            return type.IsPrimitive || 
                   type == typeof(string) || 
                   type.IsEnum ||
                   type == typeof(Vector2) || 
                   type == typeof(Vector3) || 
                   type == typeof(Vector4) || 
                   type == typeof(Quaternion) || 
                   type == typeof(Color) ||
                   type == typeof(Rect);
        }

        private object FormatValue(object val)
        {
            if (val == null) return null;
            if (val is Vector2 v2) return new { x = v2.x, y = v2.y };
            if (val is Vector3 v3) return new { x = v3.x, y = v3.y, z = v3.z };
            if (val is Vector4 v4) return new { x = v4.x, y = v4.y, z = v4.z, w = v4.w };
            if (val is Quaternion q) return new { x = q.x, y = q.y, z = q.z, w = q.w };
            if (val is Color c) return new { r = c.r, g = c.g, b = c.b, a = c.a };
            return val;
        }
    }
}
