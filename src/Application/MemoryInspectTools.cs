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
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// Runtime memory inspection tools: read local variables and parameters from
	/// a paused debug session frame using the IDbgDotNetRuntime evaluation API.
	/// </summary>
	[Export(typeof(MemoryInspectTools))]
	public sealed class MemoryInspectTools {
		readonly Lazy<DbgManager> dbgManager;
		readonly Lazy<DbgLanguageService> languageService;

		[ImportingConstructor]
		public MemoryInspectTools(
			Lazy<DbgManager> dbgManager,
			Lazy<DbgLanguageService> languageService) {
			this.dbgManager = dbgManager;
			this.languageService = languageService;
		}

		// ── get_local_variables ──────────────────────────────────────────────────

		/// <summary>
		/// Reads all local variables and parameters from the current (or specified) stack frame.
		/// Requires the debugger to be paused (breakpoint/step).
		/// Arguments: frame_index (int, optional, default=0), process_id (int, optional)
		/// </summary>
		public CallToolResult GetLocalVariables(Dictionary<string, object>? arguments) {
			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active. Start a debug session first.");

			int frameIndex = 0;
			if (arguments != null && arguments.TryGetValue("frame_index", out var fiObj)) {
				if (fiObj is JsonElement fiElem) fiElem.TryGetInt32(out frameIndex);
				else int.TryParse(fiObj?.ToString(), out frameIndex);
			}

			int? filterPid = null;
			if (arguments != null && arguments.TryGetValue("process_id", out var pidObj) &&
				pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			// All evaluation must happen on the UI/debugger dispatcher thread
			return System.Windows.Application.Current.Dispatcher.Invoke(() => {
				var process = filterPid.HasValue
					? mgr.Processes.FirstOrDefault(p => p.Id == filterPid.Value)
					: mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused)
					  ?? mgr.Processes.FirstOrDefault();

				if (process == null)
					throw new InvalidOperationException("No debugged process found.");

				if (process.State != DbgProcessState.Paused)
					throw new InvalidOperationException(
						$"Process {process.Id} is not paused. Set a breakpoint and wait for it to hit.");

				// Find current thread (prefer the one with a stack)
				var thread = process.Threads.FirstOrDefault(t => t.GetTopStackFrame() != null)
							 ?? process.Threads.FirstOrDefault();
				if (thread == null)
					throw new InvalidOperationException("No threads found in the process.");

				// Get frames
				var frames = thread.GetFrames(frameIndex + 1);
				if (frames.Length <= frameIndex)
					throw new ArgumentException(
						$"frame_index {frameIndex} out of range (process has {frames.Length} frame(s)).");

				var frame = frames[frameIndex];
				var runtime = frame.Runtime;

				// Get language and create evaluation context
				var language = languageService.Value.GetCurrentLanguage(runtime.RuntimeKindGuid);
				DbgEvaluationContext? context = null;
				try {
					context = language.CreateContext(runtime, frame.Location,
						DbgEvaluationContextOptions.None,
						TimeSpan.FromSeconds(3));

					var evalInfo = new DbgEvaluationInfo(context, frame);

					if (runtime.InternalRuntime is not IDbgDotNetRuntime dotNetRuntime)
						throw new InvalidOperationException("Runtime is not a .NET runtime.");

					// Get method metadata for local names/types
					var method = dotNetRuntime.GetFrameMethod(evalInfo);

					var locals = new List<object>();
					var parameters = new List<object>();

					if (method != null) {
						// Parameters
						var paramInfos = method.GetParameters();
						for (int i = 0; i < paramInfos.Count; i++) {
							var p = paramInfos[i];
							var valResult = dotNetRuntime.GetParameterValue(evalInfo, (uint)i);
							parameters.Add(new {
								Index = i,
								Name = p.Name ?? $"param_{i}",
								Type = p.ParameterType?.FullName ?? "?",
								Value = FormatValueResult(valResult, process)
							});
							valResult.Value?.Dispose();
						}

						// Locals
						var body = method.GetMethodBody();
						if (body != null) {
							foreach (var local in body.LocalVariables) {
								var valResult = dotNetRuntime.GetLocalValue(evalInfo, (uint)local.LocalIndex);
								locals.Add(new {
									Index = local.LocalIndex,
									Name = $"V_{local.LocalIndex}",
									Type = local.LocalType?.FullName ?? "?",
									IsPinned = local.IsPinned,
									Value = FormatValueResult(valResult, process)
								});
								valResult.Value?.Dispose();
							}
						}
					}

					// Frame info
					string frameDesc = frame.Module != null
						? $"{method?.Name ?? "?"} in {frame.Module.Name}"
						: method?.ToString() ?? "Unknown frame";

					var result = JsonSerializer.Serialize(new {
						FrameIndex = frameIndex,
						Frame = frameDesc,
						FunctionToken = $"0x{frame.FunctionToken:X8}",
						FunctionOffset = $"0x{frame.FunctionOffset:X}",
						Parameters = parameters,
						ParameterCount = parameters.Count,
						Locals = locals,
						LocalCount = locals.Count,
						Note = locals.Count == 0 && parameters.Count == 0
							? "No locals/parameters found. The method may be optimized or native."
							: null
					}, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = result } }
					};
				}
				finally {
					context?.Close();
					// Note: frame is owned by thread.GetFrames() and should be closed
					foreach (var f in frames)
						f.Close();
				}
			});
		}

		// ── Helpers ──────────────────────────────────────────────────────────────

		static object FormatValueResult(DbgDotNetValueResult result, DbgProcess process) {
			if (result.HasError)
				return new { Error = result.ErrorMessage ?? "Unknown error" };
			if (result.ValueIsException)
				return new { Exception = result.Value?.Type?.FullName ?? "Exception" };
			if (result.Value == null)
				return new { Error = "null result" };

			return FormatValue(result.Value, process);
		}

		static object FormatValue(DbgDotNetValue value, DbgProcess process) {
			if (value.IsNull)
				return new { Raw = (object?)"null", Type = value.Type?.FullName };

			var raw = value.GetRawValue();

			if (raw.HasRawValue && raw.ValueType != DbgSimpleValueType.Other) {
				// Primitive or string
				var strVal = raw.ValueType == DbgSimpleValueType.StringUtf16
					? (raw.RawValue as string ?? "null")
					: raw.RawValue?.ToString() ?? "null";
				return new { Raw = (object?)strVal, Type = value.Type?.FullName, Kind = raw.ValueType.ToString() };
			}

			// Complex object: return type and memory address if available
			var addrVal = value.GetRawAddressValue(onlyDataAddress: true);
			if (addrVal.HasValue)
				return new { Type = value.Type?.FullName, Address = $"0x{addrVal.Value.Address:X16}", Length = addrVal.Value.Length, Kind = "Object" };

			// Array?
			if (value.GetArrayCount(out uint elemCount))
				return new { Type = value.Type?.FullName, Kind = "Array", ElementCount = elemCount };

			return new { Type = value.Type?.FullName, Kind = "Complex" };
		}
	}
}
