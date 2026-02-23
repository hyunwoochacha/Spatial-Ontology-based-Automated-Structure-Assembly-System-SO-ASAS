using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp;
using WebSocketSharp.Server;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;

namespace RevitBridgeAddin
{
    /// <summary>
    /// WebSocket MCP server (ws://HOST:PORT/mcp)
    /// </summary>
    public class WsMcpServer
    {
        private WebSocketServer _wssv;

        public void Start(string host, int port)
        {
            _wssv = new WebSocketServer($"ws://{host}:{port}");
            _wssv.AddWebSocketService<McpBehavior>("/mcp");
            _wssv.Start();
        }

        public void Stop() => _wssv?.Stop();
    }

    // JSON-RPC 2.0 envelope
    class RpcRequest
    {
        public string jsonrpc;
        public string method;
        public JToken @params;
        public string id;
    }

    class RpcResponse
    {
        public string jsonrpc = "2.0";
        public object result;
        public object error;
        public string id;
    }

    /// <summary>
    /// MCP protocol handler
    /// </summary>
    public class McpBehavior : WebSocketBehavior
    {
        // MCP tool definitions
        private static readonly JArray TOOL_LIST = JArray.FromObject(new object[]
        {
            new {
                name = "ping",
                description = "Health check.",
                inputSchema = new { type="object", properties = new { }, required = new string[]{} }
            },
            new {
                name = "arrangePier",
                description = "Combine/arrange pier elements. Args: { ttlPath: string, selectedPier?: string }",
                inputSchema = new {
                    type = "object",
                    properties = new {
                        ttlPath = new { type="string" },
                        selectedPier = new { type="string" }
                    },
                    required = new [] { "ttlPath" }
                }
            },
            new {
                name = "arrangeAbutment",
                description = "Combine/arrange abutment elements. Args: { ttlPath: string, selectedAbutment?: string }",
                inputSchema = new {
                    type = "object",
                    properties = new {
                        ttlPath = new { type="string" },
                        selectedAbutment = new { type="string" }
                    },
                    required = new [] { "ttlPath" }
                }
            },
            new {
                name = "arrangeSuperstructure",
                description = "Arrange superstructure elements. Args: { ttlPath: string }",
                inputSchema = new {
                    type = "object",
                    properties = new {
                        ttlPath = new { type="string" }
                    },
                    required = new [] { "ttlPath" }
                }
            },
        });

        protected override void OnMessage(MessageEventArgs e)
        {
            var resp = new RpcResponse();
            try
            {
                var req = JsonConvert.DeserializeObject<RpcRequest>(e.Data);
                resp.id = req?.id;

                switch (req?.method)
                {
                    case "initialize":
                        resp.result = new
                        {
                            protocolVersion = "2024-11-05",
                            capabilities = new { tools = new { list = true, call = true } },
                            serverInfo = new { name = "revit-mcp", version = "0.1.0" }
                        };
                        break;

                    case "tools/list":
                        resp.result = new { tools = TOOL_LIST };
                        break;

                    case "tools/call":
                        HandleToolsCall(req, resp);
                        break;

                    default:
                        resp.error = new { code = -32601, message = "Method not found" };
                        break;
                }
            }
            catch (Exception ex)
            {
                resp.error = new { code = -32000, message = ex.Message };
            }

            Send(JsonConvert.SerializeObject(resp));
        }

        private void HandleToolsCall(RpcRequest req, RpcResponse resp)
        {
            string toolName = req.@params?["name"]?.ToString();
            JObject args = (JObject)(req.@params?["arguments"] ?? new JObject());

            // ping
            if (toolName == "ping")
            {
                resp.result = new { content = new[] { new { type = "text", text = "pong" } } };
                return;
            }

            // Get UIApplication (set from sender in Idling event)
            UIApplication uiapp = RevitServices.UIApp;
            if (uiapp == null || uiapp.ActiveUIDocument == null)
            {
                resp.error = new { code = -32001, message = "No active Revit document." };
                return;
            }

            // Tool switch
            switch (toolName)
            {
                case "arrangePier":
                    {
                        string ttl = args.Value<string>("ttlPath");
                        string sel = args.Value<string>("selectedPier"); // nullable

                        if (string.IsNullOrWhiteSpace(ttl))
                        {
                            resp.error = new { code = -32602, message = "Missing argument: ttlPath" };
                            break;
                        }

                        QueueAction(() =>
                        {
                            try
                            {
                                BridgeArrangeService.ArrangePier(uiapp, ttl, sel);
                            }
                            catch (Exception ex)
                            {
                                TaskDialog.Show("MCP arrangePier", $"Error: {ex.Message}");
                            }
                        });

                        resp.result = new { content = new[] { new { type = "text", text = "queued arrangePier" } } };
                        break;
                    }

                case "arrangeAbutment":
                    {
                        string ttl = args.Value<string>("ttlPath");
                        string sel = args.Value<string>("selectedAbutment");

                        if (string.IsNullOrWhiteSpace(ttl))
                        {
                            resp.error = new { code = -32602, message = "Missing argument: ttlPath" };
                            break;
                        }

                        QueueAction(() =>
                        {
                            try
                            {
                                BridgeArrangeService.ArrangeAbutment(uiapp, ttl, sel);
                            }
                            catch (Exception ex)
                            {
                                TaskDialog.Show("MCP arrangeAbutment", $"Error: {ex.Message}");
                            }
                        });

                        resp.result = new { content = new[] { new { type = "text", text = "queued arrangeAbutment" } } };
                        break;
                    }

                case "arrangeSuperstructure":
                    {
                        string ttl = args.Value<string>("ttlPath");

                        if (string.IsNullOrWhiteSpace(ttl))
                        {
                            resp.error = new { code = -32602, message = "Missing argument: ttlPath" };
                            break;
                        }

                        QueueAction(() =>
                        {
                            try
                            {
                                BridgeArrangeService.ArrangeSuperstructure(uiapp, ttl);
                            }
                            catch (Exception ex)
                            {
                                TaskDialog.Show("MCP arrangeSuperstructure", $"Error: {ex.Message}");
                            }
                        });

                        resp.result = new { content = new[] { new { type = "text", text = "queued arrangeSuperstructure" } } };
                        break;
                    }

                default:
                    resp.error = new { code = -32601, message = $"Unknown tool {toolName}" };
                    break;
            }
        }

        // ======== UI thread action queue (using Idling) ========
        private static readonly Queue<Action> _q = new Queue<Action>();
        private static bool _hooked = false;

        private static void QueueAction(Action a)
        {
            _q.Enqueue(a);
            var uiapp = RevitServices.UIApp;
            // If UIApplication not yet available, set via sender in Idling
            if (!_hooked && uiapp != null)
            {
                uiapp.Idling += OnIdling;
                _hooked = true;
            }
        }

        private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            // Initial UIApplication setup
            if (RevitServices.UIApp == null && sender is UIApplication ui)
                RevitServices.Set(ui);

            if (_q.Count == 0)
            {
                ((UIApplication)sender).Idling -= OnIdling;
                _hooked = false;
                return;
            }

            var action = _q.Dequeue();
            try { action(); } catch { /* swallow to keep loop */ }
        }
    }

    /// <summary>
    /// Helper to store active UIApplication (set from Idling event)
    /// </summary>
    public static class RevitServices
    {
        public static UIApplication UIApp { get; private set; }
        public static void Set(UIApplication app) => UIApp = app;
    }

    /// <summary>
    /// Service for combine/arrange logic.
    /// Currently shows messages only. Move IExternalCommand logic here
    /// to enable execution without direct Execute calls.
    /// </summary>
    public static class BridgeArrangeService
    {
        public static void ArrangePier(UIApplication uiapp, string ttlPath, string selectedPier = null)
        {
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            using (Transaction tx = new Transaction(doc, "Arrange Pier"))
            {
                tx.Start();
                // TODO: Implement actual combine/arrange logic using ttlPath/selectedPier
                tx.Commit();
            }

            TaskDialog.Show("MCP", $"Pier arranged.\nTTL: {ttlPath}\nSelected: {selectedPier ?? "(none)"}");
        }

        public static void ArrangeAbutment(UIApplication uiapp, string ttlPath, string selectedAbutment = null)
        {
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            using (Transaction tx = new Transaction(doc, "Arrange Abutment"))
            {
                tx.Start();
                // TODO: Implement actual combine/arrange logic
                tx.Commit();
            }

            TaskDialog.Show("MCP", $"Abutment arranged.\nTTL: {ttlPath}\nSelected: {selectedAbutment ?? "(none)"}");
        }

        public static void ArrangeSuperstructure(UIApplication uiapp, string ttlPath)
        {
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            using (Transaction tx = new Transaction(doc, "Arrange Superstructure"))
            {
                tx.Start();
                // TODO: Implement actual superstructure arrangement logic
                tx.Commit();
            }

            TaskDialog.Show("MCP", $"Superstructure arranged.\nTTL: {ttlPath}");
        }
    }
}
