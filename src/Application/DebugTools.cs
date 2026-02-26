/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Metadata;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// Debugger integration tools: get state, manage breakpoints, control execution, and inspect the call stack.
	/// Requires the Debugger extension to be loaded. Operations that need a paused debugger will return
	/// descriptive errors when the debugger is not in the required state.
	/// </summary>
	[Export(typeof(DebugTools))]
	public sealed class DebugTools {
		readonly Lazy<DbgManager> dbgManager;
		readonly Lazy<DbgCodeBreakpointsService> breakpointsService;
		readonly Lazy<DbgDotNetBreakpointFactory> breakpointFactory;
		readonly IDocumentTreeView documentTreeView;
		readonly Lazy<AttachableProcessesService> attachableProcessesService;

		[ImportingConstructor]
		public DebugTools(
			Lazy<DbgManager> dbgManager,
			Lazy<DbgCodeBreakpointsService> breakpointsService,
			Lazy<DbgDotNetBreakpointFactory> breakpointFactory,
			IDocumentTreeView documentTreeView,
			Lazy<AttachableProcessesService> attachableProcessesService) {
			this.dbgManager = dbgManager;
			this.breakpointsService = breakpointsService;
			this.breakpointFactory = breakpointFactory;
			this.documentTreeView = documentTreeView;
			this.attachableProcessesService = attachableProcessesService;
		}

		/// <summary>
		/// Returns the current debugger state.
		/// Arguments: none
		/// </summary>
		public CallToolResult GetDebuggerState() {
			try {
				var mgr = dbgManager.Value;
				var processes = mgr.Processes.Select(p => new {
					Id = p.Id,
					State = p.State.ToString(),
					IsRunning = p.IsRunning,
					RuntimeCount = p.Runtimes.Length,
					ThreadCount = p.Threads.Length
				}).ToList();

				var result = JsonSerializer.Serialize(new {
					IsDebugging = mgr.IsDebugging,
					IsRunning = mgr.IsRunning,
					ProcessCount = processes.Count,
					Processes = processes
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "GetDebuggerState failed");
				var result = JsonSerializer.Serialize(new {
					IsDebugging = false,
					IsRunning = (bool?)null,
					ProcessCount = 0,
					Processes = Array.Empty<object>(),
					Error = ex.Message
				}, new JsonSerializerOptions { WriteIndented = true });
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
		}

		/// <summary>
		/// Lists all code breakpoints currently registered in dnSpy.
		/// Arguments: none
		/// </summary>
		public CallToolResult ListBreakpoints() {
			try {
				var service = breakpointsService.Value;
				var bps = service.VisibleBreakpoints.Select((bp, idx) => new {
					Index = idx,
					IsEnabled = bp.IsEnabled,
					IsHidden = bp.IsHidden,
					BoundCount = bp.BoundBreakpoints.Length,
					LocationType = bp.Location?.Type ?? "Unknown",
					LocationString = bp.Location?.ToString() ?? "Unknown",
					Labels = bp.Labels?.ToArray() ?? Array.Empty<string>()
				}).ToList();

				var result = JsonSerializer.Serialize(new {
					BreakpointCount = bps.Count,
					Breakpoints = bps
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "ListBreakpoints failed");
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"Error listing breakpoints: {ex.Message}" } },
					IsError = true
				};
			}
		}

		/// <summary>
		/// Sets a breakpoint at a method entry point (IL offset 0 by default).
		/// Arguments: assembly_name, type_full_name, method_name, il_offset (optional, default 0)
		/// The breakpoint persists across debug sessions via dnSpy's breakpoint storage.
		/// </summary>
		public CallToolResult SetBreakpoint(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");
			if (!arguments.TryGetValue("method_name", out var methodNameObj))
				throw new ArgumentException("method_name is required");

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			var method = type.Methods.FirstOrDefault(m => m.Name.String == (methodNameObj.ToString() ?? ""));
			if (method == null)
				throw new ArgumentException($"Method not found: {methodNameObj}");

			uint ilOffset = 0;
			if (arguments.TryGetValue("il_offset", out var offsetObj) && offsetObj is JsonElement offsetElem)
				offsetElem.TryGetUInt32(out ilOffset);

			var module = method.Module;
			var moduleId = ModuleId.CreateFromFile(module);
			var token = method.MDToken.Raw;

			try {
				var bp = breakpointFactory.Value.Create(moduleId, token, ilOffset);
				if (bp == null) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = $"A breakpoint already exists at {method.FullName} +IL_{ilOffset:X4}" } }
					};
				}

				var result = JsonSerializer.Serialize(new {
					Success = true,
					Method = method.FullName,
					ILOffset = ilOffset,
					Token = $"0x{token:X8}",
					ModulePath = module.Location,
					IsEnabled = bp.IsEnabled
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "SetBreakpoint failed");
				throw new Exception($"Failed to set breakpoint at {method.FullName}: {ex.Message}");
			}
		}

		/// <summary>
		/// Removes a breakpoint from a method.
		/// Arguments: assembly_name, type_full_name, method_name, il_offset (optional, default 0)
		/// </summary>
		public CallToolResult RemoveBreakpoint(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");
			if (!arguments.TryGetValue("method_name", out var methodNameObj))
				throw new ArgumentException("method_name is required");

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			var method = type.Methods.FirstOrDefault(m => m.Name.String == (methodNameObj.ToString() ?? ""));
			if (method == null)
				throw new ArgumentException($"Method not found: {methodNameObj}");

			uint ilOffset = 0;
			if (arguments.TryGetValue("il_offset", out var offsetObj) && offsetObj is JsonElement offsetElem)
				offsetElem.TryGetUInt32(out ilOffset);

			var module = method.Module;
			var moduleId = ModuleId.CreateFromFile(module);
			var token = method.MDToken.Raw;

			var bp = breakpointFactory.Value.TryGetBreakpoint(moduleId, token, ilOffset);
			if (bp == null) {
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"No breakpoint found at {method.FullName} +IL_{ilOffset:X4}" } }
				};
			}

			breakpointsService.Value.Remove(bp);

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = $"Breakpoint removed from {method.FullName} +IL_{ilOffset:X4}" } }
			};
		}

		/// <summary>
		/// Removes all visible breakpoints.
		/// Arguments: none
		/// </summary>
		public CallToolResult ClearAllBreakpoints() {
			try {
				breakpointsService.Value.Clear();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = "All breakpoints cleared." } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to clear breakpoints: {ex.Message}");
			}
		}

		/// <summary>
		/// Resumes execution of all paused processes.
		/// Arguments: none
		/// </summary>
		public CallToolResult ContinueDebugger() {
			try {
				dbgManager.Value.RunAll();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = "Debugger resumed (RunAll called)." } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to continue debugger: {ex.Message}");
			}
		}

		/// <summary>
		/// Pauses all running processes.
		/// Arguments: none
		/// </summary>
		public CallToolResult BreakDebugger() {
			try {
				dbgManager.Value.BreakAll();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = "Debugger paused (BreakAll called)." } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to break debugger: {ex.Message}");
			}
		}

		/// <summary>
		/// Stops all active debug sessions.
		/// Arguments: none
		/// </summary>
		public CallToolResult StopDebugging() {
			try {
				dbgManager.Value.StopDebuggingAll();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = "All debug sessions stopped." } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to stop debugging: {ex.Message}");
			}
		}

		/// <summary>
		/// Returns the call stack of the current (or first paused) thread.
		/// Arguments: none — debugger must be paused.
		/// </summary>
		public CallToolResult GetCallStack() {
			try {
				var mgr = dbgManager.Value;

				if (!mgr.IsDebugging) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = "Debugger is not active. Start debugging first." } }
					};
				}

				// Prefer the currently selected thread; fall back to the first paused thread.
				DbgThread? currentThread = mgr.CurrentThread?.Current;

				if (currentThread == null) {
					var pausedProcess = mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused);
					if (pausedProcess == null) {
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = "No paused process found. Debugger may still be running. Use break_debugger first." } }
						};
					}
					currentThread = pausedProcess.Threads.FirstOrDefault();
				}

				if (currentThread == null) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = "No thread is available." } }
					};
				}

				const int maxFrames = 50;
				var frames = new List<object>();
				var stackFrames = currentThread.GetFrames(maxFrames);
				try {
					foreach (var frame in stackFrames) {
						try {
							frames.Add(new {
								Index = frames.Count,
								FunctionToken = $"0x{frame.FunctionToken:X8}",
								FunctionOffset = frame.FunctionOffset,
								ModuleName = frame.Module?.Name ?? "Unknown",
								IsCurrentStatement = (frame.Flags & DbgStackFrameFlags.LocationIsNextStatement) != 0
							});
						}
						finally {
							frame.Close();
						}
					}
				}
				finally {
					// GetFrames() returns owned frames; they were closed in the loop above.
				}

				var result = JsonSerializer.Serialize(new {
					ThreadId = currentThread.Id,
					ManagedId = currentThread.ManagedId,
					FrameCount = frames.Count,
					MaxFramesReturned = maxFrames,
					Frames = frames
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "GetCallStack failed");
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"Error getting call stack: {ex.Message}" } },
					IsError = true
				};
			}
		}

		// ── start_debugging ──────────────────────────────────────────────────────

		/// <summary>
		/// Launches an EXE under the dnSpy debugger.
		/// Arguments: exe_path* | arguments | working_directory | break_kind (default "EntryPoint")
		/// </summary>
		public CallToolResult StartDebugging(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("exe_path", out var exePathObj))
				throw new ArgumentException("exe_path is required");

			var exePath = exePathObj.ToString() ?? string.Empty;
			if (!System.IO.File.Exists(exePath))
				throw new ArgumentException($"File not found: {exePath}");

			string? commandLine = null;
			if (arguments.TryGetValue("arguments", out var argsObj))
				commandLine = argsObj?.ToString();

			string? workingDir = null;
			if (arguments.TryGetValue("working_directory", out var wdObj))
				workingDir = wdObj?.ToString();
			if (string.IsNullOrEmpty(workingDir))
				workingDir = System.IO.Path.GetDirectoryName(exePath);

			string breakKind = PredefinedBreakKinds.EntryPoint;
			if (arguments.TryGetValue("break_kind", out var bkObj) && bkObj?.ToString() is string bkStr && !string.IsNullOrEmpty(bkStr))
				breakKind = bkStr;

			var opts = new DotNetFrameworkStartDebuggingOptions {
				Filename         = exePath,
				CommandLine      = commandLine ?? string.Empty,
				WorkingDirectory = workingDir,
				BreakKind        = breakKind,
			};

			var error = dbgManager.Value.Start(opts);

			var result = JsonSerializer.Serialize(new {
				Started   = error == null,
				ExePath   = exePath,
				BreakKind = breakKind,
				Error     = error,
				Note      = error == null
					? "Process launched asynchronously. Use get_debugger_state to check when it is paused."
					: null
			}, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		// ── attach_to_process ────────────────────────────────────────────────────

		/// <summary>
		/// Attaches the dnSpy debugger to a running .NET process by PID.
		/// Arguments: process_id*
		/// </summary>
		public CallToolResult AttachToProcess(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("process_id", out var pidObj))
				throw new ArgumentException("process_id is required");

			int pid = 0;
			if (pidObj is JsonElement pidElem) pidElem.TryGetInt32(out pid);
			else int.TryParse(pidObj?.ToString(), out pid);
			if (pid <= 0)
				throw new ArgumentException("process_id must be a positive integer");

			var processes = attachableProcessesService.Value
				.GetAttachableProcessesAsync(null, new[] { pid }, null)
				.GetAwaiter().GetResult();

			if (processes.Length == 0)
				throw new ArgumentException(
					$"No attachable .NET process found with PID {pid}. " +
					"The process may not exist or does not expose a supported runtime.");

			// Attach to the first matching entry (each entry represents one CLR runtime in the process)
			var target = processes[0];
			target.Attach();

			var result = JsonSerializer.Serialize(new {
				Attached    = true,
				ProcessId   = pid,
				RuntimeName = target.RuntimeName,
				Name        = target.Name,
				Note        = "Attach is asynchronous. Use get_debugger_state to verify the session."
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		// ── Helpers ─────────────────────────────────────────────────────────────

		AssemblyDef? FindAssemblyByName(string name) =>
			documentTreeView.GetAllModuleNodes()
				.Select(m => m.Document?.AssemblyDef)
				.FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));

		TypeDef? FindTypeInAssembly(AssemblyDef assembly, string fullName) =>
			assembly.Modules
				.SelectMany(m => m.Types)
				.FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));
	}
}
