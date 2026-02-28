/*
    Copyright (C) 2026 @chichicaste

    This file is part of dnSpy MCP Server module. 

    dnSpy MCP Server is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy MCP Server is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy MCP Server.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using dnSpy.MCP.Server.Presentation;
using dnSpy.MCP.Server.Application;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Communication {
	/// <summary>
	/// HTTP server implementing the Model Context Protocol (MCP) for exposing dnSpy analysis tools to AI assistants.
	/// Uses an HttpListener hosted on localhost to keep dependencies minimal.
	/// </summary>
	[Export(typeof(McpServer))]
	public sealed class McpServer : IDisposable {
		readonly McpSettings settings;
		readonly McpTools tools;
		readonly BepInExResources bepinexResources;
		HttpListener? httpListener;
		readonly List<SseClient> sseClients = new List<SseClient>();
		readonly Dictionary<string, SseClient> sessionClients = new Dictionary<string, SseClient>();
		readonly object sseClientsLock = new object();
		CancellationTokenSource? cts;

		// JSON serialization options to ignore null values (JSON-RPC 2.0 requirement)
		static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions {
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		// UTF-8 without BOM — SSE clients break on the BOM that Encoding.UTF8 emits
		static readonly UTF8Encoding utf8NoBom = new UTF8Encoding(false);

		/// <summary>
		/// Initializes the MCP server with the specified settings, tools, and documentation.
		/// </summary>
		[ImportingConstructor]
		public McpServer(McpSettings settings, McpTools tools, BepInExResources bepinexResources) {
			this.settings = settings;
			this.tools = tools;
			this.bepinexResources = bepinexResources;
		}

		/// <summary>
		/// Starts the MCP server if enabled in settings.
		/// </summary>
		public void Start() {
			McpLogger.Debug($"Start() called - EnableServer={settings.EnableServer}");

			if (!settings.EnableServer) {
				McpLogger.Info("Server not enabled in settings - skipping start");
				return;
			}

			if (httpListener != null) {
				McpLogger.Warning($"Server is already running on {settings.Host}:{settings.Port}");
				return;
			}

			McpLogger.Info($"═══════════════════════════════════════════════════════");
			McpLogger.Info($"Starting MCP Server");
			McpLogger.Info($"Host: {settings.Host}");
			McpLogger.Info($"Port: {settings.Port}");
			McpLogger.Info($"═══════════════════════════════════════════════════════");

			try {
				cts = new CancellationTokenSource();

				StartHttpListenerServer();
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Failed to start server");
			}
		}

		void StartHttpListenerServer() {
			Task.Run(() => {
				int port = settings.Port;
				int maxAttempts = 10;
				HttpListener? listener = null;
				string? boundPrefix = null;

				for (int attempt = 0; attempt < maxAttempts; attempt++) {
					try {
						int currentPort = port + attempt;
						McpLogger.Debug($"Attempting to start HTTP server on port {currentPort}");

						listener = new HttpListener();
						boundPrefix = $"http://localhost:{currentPort}/";
						
						try {
							listener.Prefixes.Add(boundPrefix);
						}
						catch (HttpListenerException ex) {
							McpLogger.Debug($"Failed to add prefix {boundPrefix}: {ex.Message}");
							listener.Close();
							listener = null;
							continue;
						}
						
						listener.Start();
						
						// Success!
						port = currentPort;
						httpListener = listener;
						
						McpLogger.Info($"HttpListener server started on localhost:{port}");
						
						if (attempt > 0) {
							McpLogger.Info($"Note: Original port {settings.Port} was in use, using port {port} instead");
						}
						
						McpLogger.Info("Server is ready to accept connections");
						BroadcastStatus("running");
						break;
					}
					catch (HttpListenerException ex) when (ex.ErrorCode == 5) {
						// Access denied - no point trying other ports
						McpLogger.Exception(ex, $"Access denied to port {port}. Try running: netsh http add urlacl url=http://localhost:{port}/ user=Everyone");
						break;
					}
					catch (HttpListenerException) {
						// Port in use, try next one
						McpLogger.Debug($"Port {port + attempt} is in use, trying next...");
						listener?.Close();
						listener = null;
					}
					catch (Exception ex) {
						McpLogger.Exception(ex, $"Error starting HttpListener on port {port + attempt}");
						listener?.Close();
						listener = null;
						break;
					}
				}

				if (httpListener == null) {
					McpLogger.Error($"Failed to start HTTP server after {maxAttempts} attempts");
					return;
				}

				while (!cts!.Token.IsCancellationRequested) {
					try {
						var context = httpListener.GetContext();
						McpLogger.Debug($"Accepted connection from {context.Request.RemoteEndPoint}");
						Task.Run(() => HandleHttpRequest(context), cts.Token);
					}
					catch (HttpListenerException) {
						McpLogger.Debug("HttpListener stopped (expected during shutdown)");
						break;
					}
					catch (Exception ex) {
						McpLogger.Exception(ex, "Error accepting HTTP request");
					}
				}
			}, cts!.Token);
		}

		void HandleHttpRequest(HttpListenerContext context) {
			try {
				// Enable CORS
				context.Response.AddHeader("Access-Control-Allow-Origin", "*");
				context.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
				context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-API-Key, Authorization");

				if (context.Request.HttpMethod == "OPTIONS") {
					context.Response.StatusCode = 200;
					context.Response.Close();
					return;
				}

				var path = context.Request.Url?.AbsolutePath ?? "/";

				if (path != "/health" && !IsRequestAuthorized(context.Request)) {
					context.Response.StatusCode = 401;
					context.Response.AddHeader("WWW-Authenticate", "Bearer");
					var authMsg = Encoding.UTF8.GetBytes("{\"error\":\"Unauthorized\"}");
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = authMsg.Length;
					context.Response.OutputStream.Write(authMsg, 0, authMsg.Length);
					context.Response.Close();
					return;
				}

						// SSE stream: GET /sse, /events, or /
				if (context.Request.HttpMethod == "GET" && (path == "/sse" || path == "/events" || path == "/")) {
					McpLogger.Debug($"SSE connection attempt on path: {path}");
					HandleSseRequest(context);
					return;
				}

				// MCP SSE message endpoint: POST /message or /messages with ?sessionId=
				if (context.Request.HttpMethod == "POST" && (path == "/message" || path == "/messages")) {
					HandleSseMessageRequest(context);
					return;
				}

				if (path == "/health" && context.Request.HttpMethod == "GET") {
					var healthResponse = "{\"status\":\"ok\",\"service\":\"dnSpy MCP Server\"}";
					var buffer = Encoding.UTF8.GetBytes(healthResponse);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = buffer.Length;
					context.Response.OutputStream.Write(buffer, 0, buffer.Length);
					context.Response.Close();
					return;
				}

				if (path == "/" && context.Request.HttpMethod == "POST") {
					using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
					var body = reader.ReadToEnd();

					var request = JsonSerializer.Deserialize<McpRequest>(body);
					if (request == null) {
						context.Response.StatusCode = 400;
						var errorBytes = Encoding.UTF8.GetBytes("Invalid request");
						context.Response.OutputStream.Write(errorBytes, 0, errorBytes.Length);
						context.Response.Close();
						return;
					}

					var response = HandleRequest(request);
					var responseJson = JsonSerializer.Serialize(response, jsonOptions);
					var responseBytes = Encoding.UTF8.GetBytes(responseJson);

					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = responseBytes.Length;
					context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
					context.Response.Close();
				}
				else {
					McpLogger.Warning($"Unhandled request: {context.Request.HttpMethod} {path}");
					context.Response.StatusCode = 404;
					context.Response.Close();
				}
			}
			catch (Exception ex) {
				try {
					settings.Log($"ERROR in HandleHttpRequest: {ex.GetType().Name}: {ex.Message}");
					var errorResponse = new McpResponse {
						JsonRpc = "2.0",
						Error = new McpError {
							Code = -32603,
							Message = "Internal error",
							Data = ex.Message
						}
					};

					var responseJson = JsonSerializer.Serialize(errorResponse, jsonOptions);
					var responseBytes = Encoding.UTF8.GetBytes(responseJson);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = responseBytes.Length;
					context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
					context.Response.Close();
				}
				catch {
					// Failed to send error response
				}
				// Also surface full exception to Output pane for diagnostics
				try {
					settings.LogError($"ERROR in HandleHttpRequest (stack): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
					McpOutput.SafeWriteException(ex);
				}
				catch {
					// best-effort
				}
			}
		}

		bool IsRequestAuthorized(HttpListenerRequest req) {
			var cfg = Configuration.McpConfig.Instance;
			if (!cfg.RequireApiKey || string.IsNullOrEmpty(cfg.ApiKey)) return true;
			var key = req.Headers["X-API-Key"];
			if (key == cfg.ApiKey) return true;
			var auth = req.Headers["Authorization"];
			if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
				&& auth.Substring(7) == cfg.ApiKey) return true;
			return false;
		}

		/// <summary>
		/// Restarts the MCP server.
		/// </summary>
		public void Restart() {
			McpLogger.Info("Restarting MCP server...");
			Stop();
			// Small delay to ensure clean shutdown
			Task.Delay(500).Wait();
			Start();
			McpLogger.Info("MCP server restart completed");
		}

		/// <summary>
		/// Gets the current status of the server.
		/// </summary>
		public bool IsRunning =>
			httpListener != null && httpListener.IsListening;

		/// <summary>
		/// Gets a status message for the server.
		/// </summary>
		public string GetStatusMessage() {
			// Report localhost as the bind address since the server intentionally binds to loopback.
			return IsRunning ? $"Server is running on localhost:{settings.Port}" : "Server is stopped";
		}

		/// <summary>
		/// Stops the MCP server if it's running.
		/// </summary>
		public void Stop() {
			McpLogger.Info("Stopping MCP server...");
			try {
				cts?.Cancel();
				
				// Force close the HttpListener to release the port
				if (httpListener != null) {
					try {
						httpListener.Stop();
					}
					catch { }
					try {
						httpListener.Abort();
					}
					catch { }
					httpListener.Close();
					httpListener = null;
				}
				
				CloseAllSseClients();
				BroadcastStatus("stopped");
				
				// Small delay to ensure port is released
				Thread.Sleep(100);
				
				McpLogger.Info("MCP server stopped successfully");
				cts?.Dispose();
				cts = null;
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Error stopping server");
			}
		}

		void HandleSseRequest(HttpListenerContext context) {
			if (cts == null) {
				McpLogger.Warning("SSE request rejected: CancellationTokenSource is null (server stopping)");
				context.Response.StatusCode = 503;
				context.Response.Close();
				return;
			}

			try {
				McpLogger.Info("Initializing SSE stream for client");
				var response = context.Response;
				response.StatusCode = 200;
				response.ContentType = "text/event-stream";
				response.SendChunked = true;
				response.Headers["Cache-Control"] = "no-cache";
				response.Headers["Connection"] = "keep-alive";

					McpLogger.Debug("SSE headers sent to client");
				var sessionId = Guid.NewGuid().ToString("N");
				var host = context.Request.Url?.Host ?? "localhost";
				var port = context.Request.Url?.Port ?? settings.Port;
				var endpointUrl = $"http://{host}:{port}/message?sessionId={sessionId}";
				var writer = new StreamWriter(response.OutputStream, utf8NoBom) { AutoFlush = true };
				writer.WriteLine("event: endpoint");
				writer.WriteLine($"data: {endpointUrl}");
				writer.WriteLine();
				writer.Flush();

				McpLogger.Info($"SSE client connected, sessionId={sessionId}, endpoint={endpointUrl}");
				var client = new SseClient(writer, response, sessionId);
				AddSseClient(client);
				BroadcastStatus("running"); // push latest state immediately

				McpLogger.Debug("Starting SSE heartbeat task");
				Task.Run(() => RunSseHeartbeatAsync(client, cts.Token));
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Failed to initialize SSE stream");
				try {
					context.Response.StatusCode = 500;
					context.Response.Close();
				}
				catch { }
			}
		}

		void HandleSseMessageRequest(HttpListenerContext context) {
		var sessionId = context.Request.QueryString["sessionId"];
		if (string.IsNullOrEmpty(sessionId)) {
			context.Response.StatusCode = 400;
			context.Response.Close();
			return;
		}

		SseClient? client;
		lock (sseClientsLock) {
			sessionClients.TryGetValue(sessionId, out client);
		}

		if (client == null || client.IsClosed) {
			McpLogger.Warning($"POST /message: unknown or closed sessionId={sessionId}");
			context.Response.StatusCode = 404;
			context.Response.Close();
			return;
		}

		string body;
		using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
			body = reader.ReadToEnd();

		// Respond 202 Accepted immediately; actual response goes via SSE stream
		context.Response.StatusCode = 202;
		context.Response.ContentLength64 = 0;
		context.Response.Close();

		var capturedClient = client;
		Task.Run(() => {
			try {
				var request = JsonSerializer.Deserialize<McpRequest>(body);
				if (request == null) {
					McpLogger.Warning("POST /message: invalid JSON-RPC body");
					return;
				}
				var response = HandleRequest(request);
				var json = JsonSerializer.Serialize(response, jsonOptions);
				capturedClient.SendMessage(json);
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Error processing SSE message");
			}
		});
	}

	async Task RunSseHeartbeatAsync(SseClient client, CancellationToken token) {
			try {
				while (!token.IsCancellationRequested && !client.IsClosed) {
					await Task.Delay(TimeSpan.FromSeconds(15), token);
					if (token.IsCancellationRequested || client.IsClosed)
						break;

					client.WriteComment("heartbeat");
				}
			}
			catch (TaskCanceledException) {
				// expected during shutdown
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Heartbeat loop failed for SSE client");
			}
			finally {
				RemoveSseClient(client);
			}
		}

		void BroadcastStatus(string status) {
			lock (sseClientsLock) {
				if (sseClients.Count == 0)
					return;
				var payload = $"{{\"status\":\"{status}\",\"timestamp\":\"{DateTime.UtcNow:O}\"}}";
				foreach (var client in sseClients.ToArray()) {
					try {
						client.WriteEvent("status", payload);
					}
					catch {
						client.Dispose();
						sseClients.Remove(client);
					}
				}
			}
		}

		void AddSseClient(SseClient client) {
			lock (sseClientsLock) {
				sseClients.Add(client);
				if (!string.IsNullOrEmpty(client.SessionId))
					sessionClients[client.SessionId] = client;
			}
		}

		void RemoveSseClient(SseClient client) {
			lock (sseClientsLock) {
				if (sseClients.Remove(client)) {
					if (!string.IsNullOrEmpty(client.SessionId))
						sessionClients.Remove(client.SessionId);
					client.Dispose();
				}
			}
		}

		void CloseAllSseClients() {
			lock (sseClientsLock) {
				foreach (var client in sseClients)
					client.Dispose();
				sseClients.Clear();
				sessionClients.Clear();
			}
		}

		sealed class SseClient : IDisposable {
			readonly StreamWriter writer;
			readonly HttpListenerResponse response;
			readonly object writeLock = new object();
			bool isClosed;

			public string SessionId { get; }

			public bool IsClosed {
				get {
					lock (writeLock) {
						return isClosed;
					}
				}
			}

			public SseClient(StreamWriter writer, HttpListenerResponse response, string sessionId) {
				this.writer = writer;
				this.response = response;
				SessionId = sessionId;
			}

			public void SendMessage(string json) {
				lock (writeLock) {
					if (isClosed)
						return;
					writer.WriteLine("event: message");
					writer.WriteLine($"data: {json}");
					writer.WriteLine();
					writer.Flush();
				}
			}

			public void WriteEvent(string eventName, string data) {
				lock (writeLock) {
					if (isClosed)
						return;
					writer.WriteLine($"event: {eventName}");
					writer.WriteLine($"data: {data}");
					writer.WriteLine();
					writer.Flush();
				}
			}

			public void WriteComment(string comment) {
				lock (writeLock) {
					if (isClosed)
						return;
					writer.WriteLine($": {comment}");
					writer.WriteLine();
					writer.Flush();
				}
			}

			public void Dispose() {
				lock (writeLock) {
					if (isClosed)
						return;
					isClosed = true;
					try {
						writer.Dispose();
					}
					catch { }
					try {
						response.OutputStream.Close();
					}
					catch { }
					try {
						response.Close();
					}
					catch { }
				}
			}
		}

		McpResponse HandleRequest(McpRequest request) {
			try {
				// Handle notifications (no response needed)
				if (request.Method.StartsWith("notifications/")) {
					McpLogger.Debug($"Received notification: {request.Method}");
					return new McpResponse {
						JsonRpc = "2.0",
						Id = request.Id,
						Result = new { }
					};
				}

				McpLogger.Info($"Handling MCP request: {request.Method}");

				var result = request.Method switch {
					"initialize" => HandleInitialize(),
					"ping" => HandlePing(),
					"tools/list" => HandleListTools(),
					"tools/call" => HandleCallTool(request.Params),
					"resources/list" => HandleListResources(),
					"resources/read" => HandleReadResource(request.Params),
					_ => throw new Exception($"Unknown method: {request.Method}")
				};

				McpLogger.Debug($"Request {request.Method} completed successfully");

				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Result = result
				};
			}
			catch (ArgumentException ex) {
				// ArgumentException indicates invalid parameters (MCP error code -32602)
				McpLogger.Warning($"Invalid parameters for {request.Method}: {ex.Message}");
				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Error = new McpError {
						Code = -32602,
						Message = ex.Message
					}
				};
			}
			catch (Exception ex) {
				// Other exceptions are internal errors (MCP error code -32603)
				McpLogger.Exception(ex, $"Error handling request {request.Method}");
				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Error = new McpError {
						Code = -32603,
						Message = ex.Message
					}
				};
			}
		}

		object HandleInitialize() {
			return new InitializeResult {
				ProtocolVersion = "2024-11-05",
				Capabilities = new ServerCapabilities {
					Tools = new Dictionary<string, object>(),
					Resources = new Dictionary<string, object>()
				},
				ServerInfo = new ServerInfo {
					Name = "dnSpy MCP Server",
					Version = "1.0.0"
				}
			};
		}

		object HandlePing() {
			// Simple ping/pong for keepalive
			return new { };
		}

		object HandleListTools() {
			return new ListToolsResult {
				Tools = tools.GetAvailableTools()
			};
		}

		object HandleCallTool(Dictionary<string, object>? parameters) {
			if (parameters == null)
				throw new ArgumentException("Parameters required");

			var toolCallJson = JsonSerializer.Serialize(parameters);
			var toolCall = JsonSerializer.Deserialize<CallToolRequest>(toolCallJson);

			if (toolCall == null)
				throw new ArgumentException("Invalid tool call parameters");

			return tools.ExecuteTool(toolCall.Name, toolCall.Arguments);
		}

		object HandleListResources() {
			return new ListResourcesResult {
				Resources = bepinexResources.GetResources()
			};
		}

		object HandleReadResource(Dictionary<string, object>? parameters) {
			if (parameters == null)
				throw new ArgumentException("Parameters required");

			var requestJson = JsonSerializer.Serialize(parameters);
			var readRequest = JsonSerializer.Deserialize<ReadResourceRequest>(requestJson);

			if (readRequest == null || string.IsNullOrEmpty(readRequest.Uri))
				throw new ArgumentException("Resource URI required");

			var content = bepinexResources.ReadResource(readRequest.Uri);
			if (content == null)
				throw new ArgumentException($"Resource not found: {readRequest.Uri}");

			return new ReadResourceResult {
				Contents = new List<ResourceContent> {
					new ResourceContent {
						Uri = readRequest.Uri,
						MimeType = "text/markdown",
						Text = content
					}
				}
			};
		}

		/// <summary>
		/// Disposes the server and releases all resources.
		/// </summary>
		public void Dispose() {
			Stop();
		}
	}
}
