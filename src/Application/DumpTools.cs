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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// Memory-dump tools: enumerate modules in running processes and extract their bytes from
	/// memory.  Requires an active debug session (started via dnSpy's debug menu or
	/// attach-to-process). Most operations return descriptive errors when no debugger is active.
	/// </summary>
	[Export(typeof(DumpTools))]
	public sealed class DumpTools {
		readonly Lazy<DbgManager> dbgManager;

		[ImportingConstructor]
		public DumpTools(Lazy<DbgManager> dbgManager) {
			this.dbgManager = dbgManager;
		}

		// ── list_runtime_modules ────────────────────────────────────────────────

		/// <summary>
		/// Lists every module loaded in the currently debugged processes.
		/// Arguments: process_id (optional int), name_filter (optional substring/regex)
		/// </summary>
		public CallToolResult ListRuntimeModules(Dictionary<string, object>? arguments) {
			try {
				var mgr = dbgManager.Value;
				if (!mgr.IsDebugging) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent {
							Text = "Debugger is not active. Start a debug session first."
						}}
					};
				}

				int? filterPid = null;
				if (arguments != null && arguments.TryGetValue("process_id", out var pidRaw) && pidRaw is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
					filterPid = pidInt;

				string? nameFilter = null;
				if (arguments != null && arguments.TryGetValue("name_filter", out var nfObj))
					nameFilter = nfObj?.ToString();

				System.Text.RegularExpressions.Regex? nameRegex = null;
				if (!string.IsNullOrEmpty(nameFilter))
					nameRegex = BuildPatternRegex(nameFilter!);

				var modules = new List<object>();
				foreach (var process in mgr.Processes) {
					if (filterPid.HasValue && process.Id != filterPid.Value)
						continue;
					foreach (var runtime in process.Runtimes) {
						foreach (var module in runtime.Modules) {
							var name = module.Name ?? string.Empty;
							if (nameRegex != null &&
								!nameRegex.IsMatch(name) &&
								!nameRegex.IsMatch(Path.GetFileName(module.Filename ?? string.Empty)))
								continue;

							modules.Add(new {
								ProcessId = process.Id,
								ProcessName = process.Name,
								RuntimeName = runtime.Name,
								ModuleName = name,
								Filename = module.Filename,
								Address = module.HasAddress ? $"0x{module.Address:X16}" : "N/A",
								SizeBytes = module.HasAddress ? (long)module.Size : 0L,
								IsExe = module.IsExe,
								IsDynamic = module.IsDynamic,
								IsInMemory = module.IsInMemory,
								IsOptimized = module.IsOptimized,
								Version = module.Version,
								AppDomain = module.AppDomain?.Name ?? "None"
							});
						}
					}
				}

				var json = JsonSerializer.Serialize(new {
					ModuleCount = modules.Count,
					Modules = modules
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = json } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "ListRuntimeModules failed");
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"Error: {ex.Message}" } },
					IsError = true
				};
			}
		}

		// ── dump_module_from_memory ──────────────────────────────────────────────

		/// <summary>
		/// Dumps a loaded .NET module from the debugged process to disk.
		/// Arguments: module_name (required), output_path (required), process_id (optional int)
		/// First tries IDbgDotNetRuntime.GetRawModuleBytes (high-quality .NET bytes), then
		/// falls back to reading process memory directly via DbgProcess.ReadMemory.
		/// </summary>
		public CallToolResult DumpModuleFromMemory(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("module_name", out var moduleNameObj))
				throw new ArgumentException("module_name is required");
			if (!arguments.TryGetValue("output_path", out var outputPathObj))
				throw new ArgumentException("output_path is required");

			var moduleName = moduleNameObj.ToString() ?? string.Empty;
			var outputPath = outputPathObj.ToString() ?? string.Empty;

			if (string.IsNullOrWhiteSpace(moduleName))
				throw new ArgumentException("module_name must not be empty");
			if (string.IsNullOrWhiteSpace(outputPath))
				throw new ArgumentException("output_path must not be empty");

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active. Start a debug session first.");

			// Locate the module
			DbgModule? targetModule = null;
			DbgRuntime? targetRuntime = null;

			foreach (var process in mgr.Processes) {
				if (filterPid.HasValue && process.Id != filterPid.Value)
					continue;
				foreach (var runtime in process.Runtimes) {
					var found = runtime.Modules.FirstOrDefault(m =>
						(m.Name ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						Path.GetFileName(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase));
					if (found != null) {
						targetModule = found;
						targetRuntime = runtime;
						break;
					}
				}
				if (targetModule != null) break;
			}

			if (targetModule == null)
				throw new ArgumentException($"Module '{moduleName}' not found in any debugged process. Use list_runtime_modules to see loaded modules.");

			// Ensure output directory exists
			var outDir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(outDir))
				Directory.CreateDirectory(outDir);

			// Strategy 1: IDbgDotNetRuntime.GetRawModuleBytes — preferred for .NET modules
			if (targetRuntime!.InternalRuntime is IDbgDotNetRuntime dotNetRuntime) {
				try {
					var rawData = dotNetRuntime.GetRawModuleBytes(targetModule);
					if (rawData.RawBytes != null && rawData.RawBytes.Length > 0) {
						File.WriteAllBytes(outputPath, rawData.RawBytes);
						var json = JsonSerializer.Serialize(new {
							Success = true,
							Module = targetModule.Name,
							OutputPath = outputPath,
							SizeBytes = rawData.RawBytes.Length,
							IsFileLayout = rawData.IsFileLayout,
							Method = "IDbgDotNetRuntime.GetRawModuleBytes"
						}, new JsonSerializerOptions { WriteIndented = true });
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = json } }
						};
					}
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, $"GetRawModuleBytes failed for {moduleName}, falling back to ReadMemory");
				}
			}

			// Strategy 2: DbgProcess.ReadMemory — raw memory fallback
			if (!targetModule.HasAddress)
				throw new InvalidOperationException(
					$"Module '{moduleName}' has no mapped address and IDbgDotNetRuntime returned no bytes. " +
					"This can happen for dynamic (in-memory) modules without a file layout.");

			var moduleSize = (int)targetModule.Size;
			if (moduleSize <= 0 || moduleSize > 256 * 1024 * 1024)
				throw new InvalidOperationException($"Module size out of safe range: {moduleSize:N0} bytes.");

			var bytes = targetModule.Process.ReadMemory(targetModule.Address, moduleSize);
			File.WriteAllBytes(outputPath, bytes);

			var fallbackJson = JsonSerializer.Serialize(new {
				Success = true,
				Module = targetModule.Name,
				OutputPath = outputPath,
				SizeBytes = bytes.Length,
				IsFileLayout = false,
				Method = "DbgProcess.ReadMemory",
				Warning = "Dumped raw process memory (memory layout). " +
						  "The PE headers may be in memory-mapped form. " +
						  "Use a tool like 'pe_unmapper' or LordPE to convert to file layout before loading."
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = fallbackJson } }
			};
		}

		// ── read_process_memory ──────────────────────────────────────────────────

		/// <summary>
		/// Read raw bytes from a debugged process address and return a formatted hex dump.
		/// Arguments: address (hex string "0x7FF..." or decimal), size (1-65536 bytes),
		///            process_id (optional int)
		/// </summary>
		public CallToolResult ReadProcessMemory(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("address", out var addressObj))
				throw new ArgumentException("address is required");
			if (!arguments.TryGetValue("size", out var sizeObj))
				throw new ArgumentException("size is required");

			var addressStr = (addressObj?.ToString() ?? string.Empty).Trim();
			if (!TryParseAddress(addressStr, out ulong address))
				throw new ArgumentException($"Invalid address '{addressStr}'. Use hex (0x7FF000) or decimal.");

			int size = 0;
			if (sizeObj is JsonElement sizeElem) sizeElem.TryGetInt32(out size);
			else int.TryParse(sizeObj?.ToString(), out size);
			if (size <= 0 || size > 65536)
				throw new ArgumentException($"size must be 1..65536, got {size}.");

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			DbgProcess? process = null;
			if (filterPid.HasValue)
				process = mgr.Processes.FirstOrDefault(p => p.Id == filterPid.Value);
			else
				process = mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused)
					?? mgr.Processes.FirstOrDefault();

			if (process == null)
				throw new InvalidOperationException("No debugged process found.");

			var bytes = process.ReadMemory(address, size);

			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				Address = $"0x{address:X16}",
				SizeRequested = size,
				SizeRead = bytes.Length,
				HexDump = BuildHexDump(bytes, address),
				Base64 = Convert.ToBase64String(bytes)
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		// ── Helpers ──────────────────────────────────────────────────────────────

		static System.Text.RegularExpressions.Regex BuildPatternRegex(string pattern) {
			// If the pattern contains regex metacharacters beyond simple wildcards, treat as regex
			bool isRegex = pattern.IndexOfAny(new[] { '^', '$', '[', '(', '|', '+', '{' }) >= 0;
			if (isRegex) {
				return new System.Text.RegularExpressions.Regex(
					pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase |
							 System.Text.RegularExpressions.RegexOptions.CultureInvariant);
			}
			// Treat as glob: * → .*, ? → .
			var escaped = System.Text.RegularExpressions.Regex.Escape(pattern)
				.Replace(@"\*", ".*")
				.Replace(@"\?", ".");
			return new System.Text.RegularExpressions.Regex(
				"^" + escaped + "$",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase |
				System.Text.RegularExpressions.RegexOptions.CultureInvariant);
		}

		static bool TryParseAddress(string s, out ulong value) {
			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return ulong.TryParse(s.Substring(2),
					System.Globalization.NumberStyles.HexNumber,
					System.Globalization.CultureInfo.InvariantCulture, out value);
			if (ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
					System.Globalization.CultureInfo.InvariantCulture, out value))
				return true;
			return ulong.TryParse(s, out value);
		}

		static string BuildHexDump(byte[] bytes, ulong baseAddress) {
			const int rowWidth = 16;
			var sb = new StringBuilder(bytes.Length * 4);
			for (int i = 0; i < bytes.Length; i += rowWidth) {
				sb.Append($"{baseAddress + (ulong)i:X16}  ");
				int len = Math.Min(rowWidth, bytes.Length - i);
				for (int j = 0; j < len; j++) {
					sb.Append($"{bytes[i + j]:X2} ");
					if (j == 7) sb.Append(' ');
				}
				for (int j = len; j < rowWidth; j++) {
					sb.Append("   ");
					if (j == 7) sb.Append(' ');
				}
				sb.Append(" |");
				for (int j = 0; j < len; j++) {
					char c = (char)bytes[i + j];
					sb.Append(c >= 32 && c < 127 ? c : '.');
				}
				sb.AppendLine("|");
			}
			return sb.ToString().TrimEnd();
		}
	}
}
