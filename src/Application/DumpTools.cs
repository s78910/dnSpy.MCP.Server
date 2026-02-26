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
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
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

		// ── get_pe_sections ──────────────────────────────────────────────────────

		/// <summary>
		/// Lists the PE sections of a module loaded in the debugged process.
		/// Arguments: module_name (required), process_id (int, optional)
		/// </summary>
		public CallToolResult GetPeSections(Dictionary<string, object>? arguments) {
			if (arguments == null || !arguments.TryGetValue("module_name", out var moduleNameObj))
				throw new ArgumentException("module_name is required");

			var moduleName = moduleNameObj.ToString() ?? string.Empty;
			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			var (targetModule, _) = FindModule(mgr, moduleName, filterPid);
			if (targetModule == null)
				throw new ArgumentException($"Module '{moduleName}' not found. Use list_runtime_modules.");

			if (!targetModule.HasAddress)
				throw new InvalidOperationException($"Module '{moduleName}' has no mapped address.");

			// Read enough bytes to parse PE headers (4 KB is sufficient for section table)
			int headerSize = (int)Math.Min(targetModule.Size, 8192u);
			var headerBytes = targetModule.Process.ReadMemory(targetModule.Address, headerSize);

			try {
				using var peImage = new dnlib.PE.PEImage(headerBytes, dnlib.PE.ImageLayout.Memory, false);
				var ntHdr = peImage.ImageNTHeaders;
				bool is64 = ntHdr?.OptionalHeader?.Magic == 0x20B; // PE32+ magic
				ulong imageBase = ntHdr?.OptionalHeader?.ImageBase ?? 0;
				uint entryPoint = ntHdr?.OptionalHeader != null ? (uint)ntHdr.OptionalHeader.AddressOfEntryPoint : 0;
				var dataDirs = ntHdr?.OptionalHeader?.DataDirectories;
				bool isDotNet = dataDirs != null && dataDirs.Length > 14 &&
					(uint)dataDirs[14].VirtualAddress != 0;

				var sections = peImage.ImageSectionHeaders.Select(s => new {
					Name = s.DisplayName,
					VirtualAddress = $"0x{(uint)s.VirtualAddress:X8}",
					VirtualSize = s.VirtualSize,
					PointerToRawData = $"0x{s.PointerToRawData:X8}",
					SizeOfRawData = s.SizeOfRawData,
					Characteristics = DescribeSectionCharacteristics((uint)s.Characteristics),
					CharacteristicsRaw = $"0x{(uint)s.Characteristics:X8}"
				}).ToList();

				var result = JsonSerializer.Serialize(new {
					Module = targetModule.Name,
					BaseAddress = $"0x{targetModule.Address:X16}",
					ModuleSize = targetModule.Size,
					ImageBase = $"0x{imageBase:X16}",
					EntryPoint = $"0x{entryPoint:X8}",
					Bitness = is64 ? 64 : 32,
					IsDotNet = isDotNet,
					ImageLayout = targetModule.ImageLayout.ToString(),
					SectionCount = sections.Count,
					Sections = sections
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				throw new InvalidOperationException($"Failed to parse PE headers for '{moduleName}': {ex.Message}");
			}
		}

		// ── dump_pe_section ───────────────────────────────────────────────────────

		/// <summary>
		/// Dumps a specific PE section from a loaded module. Writes to disk and returns base64.
		/// Arguments: module_name (required), section_name (required, e.g. ".text"),
		///            output_path (optional), process_id (int, optional)
		/// </summary>
		public CallToolResult DumpPeSection(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("module_name", out var moduleNameObj))
				throw new ArgumentException("module_name is required");
			if (!arguments.TryGetValue("section_name", out var sectionNameObj))
				throw new ArgumentException("section_name is required");

			var moduleName = moduleNameObj.ToString() ?? string.Empty;
			var sectionName = sectionNameObj.ToString() ?? string.Empty;

			string? outputPath = null;
			if (arguments.TryGetValue("output_path", out var opObj))
				outputPath = opObj?.ToString();

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			var (targetModule, _) = FindModule(mgr, moduleName, filterPid);
			if (targetModule == null)
				throw new ArgumentException($"Module '{moduleName}' not found.");
			if (!targetModule.HasAddress)
				throw new InvalidOperationException($"Module '{moduleName}' has no mapped address.");

			int moduleSize = (int)targetModule.Size;
			var moduleBytes = targetModule.Process.ReadMemory(targetModule.Address, moduleSize);

			using var peImage = new dnlib.PE.PEImage(moduleBytes, dnlib.PE.ImageLayout.Memory, false);

			var section = peImage.ImageSectionHeaders.FirstOrDefault(s =>
				s.DisplayName.Equals(sectionName, StringComparison.OrdinalIgnoreCase) ||
				s.DisplayName.TrimEnd('\0').Equals(sectionName.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

			if (section == null) {
				var available = string.Join(", ", peImage.ImageSectionHeaders.Select(s => s.DisplayName));
				throw new ArgumentException($"Section '{sectionName}' not found. Available: {available}");
			}

			uint va = (uint)section.VirtualAddress;
			uint sz = Math.Max(section.VirtualSize, section.SizeOfRawData);
			sz = Math.Min(sz, (uint)(moduleBytes.Length - (int)va));

			var sectionBytes = new byte[sz];
			Array.Copy(moduleBytes, (int)va, sectionBytes, 0, (int)sz);

			if (!string.IsNullOrEmpty(outputPath)) {
				var dir = Path.GetDirectoryName(outputPath);
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
				File.WriteAllBytes(outputPath, sectionBytes);
			}

			var json = JsonSerializer.Serialize(new {
				Module = targetModule.Name,
				Section = section.DisplayName,
				VirtualAddress = $"0x{va:X8}",
				AbsoluteAddress = $"0x{targetModule.Address + va:X16}",
				SizeBytes = sz,
				OutputPath = outputPath,
				Base64 = Convert.ToBase64String(sectionBytes),
				Characteristics = DescribeSectionCharacteristics((uint)section.Characteristics)
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		// ── dump_module_unpacked ──────────────────────────────────────────────────

		/// <summary>
		/// Full module dump with optional memory→file layout conversion.
		/// Handles .NET, native, and mixed-mode modules. Preferred over dump_module_from_memory
		/// when you need a file that loads cleanly in IDA/Ghidra/dnSpy.
		/// Arguments: module_name (required), output_path (required),
		///            try_fix_pe_layout (bool, default=true), process_id (int, optional)
		/// </summary>
		public CallToolResult DumpModuleUnpacked(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("module_name", out var moduleNameObj))
				throw new ArgumentException("module_name is required");
			if (!arguments.TryGetValue("output_path", out var outputPathObj))
				throw new ArgumentException("output_path is required");

			var moduleName = moduleNameObj.ToString() ?? string.Empty;
			var outputPath = outputPathObj.ToString() ?? string.Empty;

			bool fixLayout = true;
			if (arguments.TryGetValue("try_fix_pe_layout", out var fixObj)) {
				if (fixObj is bool fb) fixLayout = fb;
				else if (fixObj is JsonElement fe) fixLayout = fe.ValueKind == JsonValueKind.True;
				else if (fixObj?.ToString()?.ToLowerInvariant() == "false") fixLayout = false;
			}

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			var (targetModule, targetRuntime) = FindModule(mgr, moduleName, filterPid);
			if (targetModule == null)
				throw new ArgumentException($"Module '{moduleName}' not found. Use list_runtime_modules.");

			var outDir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

			// Strategy A: IDbgDotNetRuntime.GetRawModuleBytes (best quality for .NET)
			if (targetRuntime?.InternalRuntime is IDbgDotNetRuntime dotNetRuntime) {
				try {
					var rawData = dotNetRuntime.GetRawModuleBytes(targetModule);
					if (rawData.RawBytes != null && rawData.RawBytes.Length > 0) {
						File.WriteAllBytes(outputPath, rawData.RawBytes);
						var j = JsonSerializer.Serialize(new {
							Success = true, Module = targetModule.Name, OutputPath = outputPath,
							SizeBytes = rawData.RawBytes.Length, IsFileLayout = rawData.IsFileLayout,
							Method = "IDbgDotNetRuntime.GetRawModuleBytes",
							Note = "High-quality .NET module bytes. Ready to load in dnSpy."
						}, new JsonSerializerOptions { WriteIndented = true });
						return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = j } } };
					}
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, $"GetRawModuleBytes failed for {moduleName}, falling back to ReadMemory");
				}
			}

			// Strategy B: ReadMemory + optional PE layout fix
			if (!targetModule.HasAddress)
				throw new InvalidOperationException($"Module '{moduleName}' has no mapped address.");

			int moduleSize = (int)targetModule.Size;
			if (moduleSize <= 0 || moduleSize > 512 * 1024 * 1024)
				throw new InvalidOperationException($"Module size out of safe range: {moduleSize:N0} bytes.");

			var bytes = targetModule.Process.ReadMemory(targetModule.Address, moduleSize);
			bool isMemoryLayout = targetModule.ImageLayout == DbgImageLayout.Memory;
			string method;
			int finalSize;

			if (fixLayout && isMemoryLayout) {
				var fixedBytes = TryConvertMemoryToFileLayout(bytes, out int fixedSize);
				if (fixedBytes != null) {
					File.WriteAllBytes(outputPath, fixedBytes.Take(fixedSize).ToArray());
					method = "ReadMemory+PELayoutFix";
					finalSize = fixedSize;
				}
				else {
					File.WriteAllBytes(outputPath, bytes);
					method = "ReadMemory (PE fix failed, raw dump)";
					finalSize = bytes.Length;
				}
			}
			else {
				File.WriteAllBytes(outputPath, bytes);
				method = "ReadMemory (raw)";
				finalSize = bytes.Length;
			}

			var result = JsonSerializer.Serialize(new {
				Success = true, Module = targetModule.Name, OutputPath = outputPath,
				OriginalSize = bytes.Length, FileLayoutSize = finalSize,
				IsFileLayout = fixLayout && isMemoryLayout,
				Method = method,
				Warning = isMemoryLayout && !fixLayout
					? "Memory layout dump. Use a PE fixer (LordPE, CFF Explorer) before analysis."
					: null
			}, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		// ── dump_memory_to_file ───────────────────────────────────────────────────

		/// <summary>
		/// Saves a raw memory range from the debugged process to a file.
		/// Complement of read_process_memory (which returns hex/base64 but has 64KB limit).
		/// Arguments: address (hex/dec), size (up to 256MB), output_path (required),
		///            process_id (int, optional)
		/// </summary>
		public CallToolResult DumpMemoryToFile(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("address", out var addressObj))
				throw new ArgumentException("address is required");
			if (!arguments.TryGetValue("size", out var sizeObj))
				throw new ArgumentException("size is required");
			if (!arguments.TryGetValue("output_path", out var outputPathObj))
				throw new ArgumentException("output_path is required");

			var addressStr = (addressObj?.ToString() ?? string.Empty).Trim();
			if (!TryParseAddress(addressStr, out ulong address))
				throw new ArgumentException($"Invalid address '{addressStr}'.");

			int size = 0;
			if (sizeObj is JsonElement sizeElem) sizeElem.TryGetInt32(out size);
			else int.TryParse(sizeObj?.ToString(), out size);
			if (size <= 0 || size > 256 * 1024 * 1024)
				throw new ArgumentException($"size must be 1..268435456 (256 MB), got {size}.");

			var outputPath = outputPathObj?.ToString() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(outputPath))
				throw new ArgumentException("output_path must not be empty.");

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			DbgProcess? process = filterPid.HasValue
				? mgr.Processes.FirstOrDefault(p => p.Id == filterPid.Value)
				: mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused)
				  ?? mgr.Processes.FirstOrDefault();

			if (process == null)
				throw new InvalidOperationException("No debugged process found.");

			var bytes = process.ReadMemory(address, size);

			var dir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			File.WriteAllBytes(outputPath, bytes);

			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				Address = $"0x{address:X16}",
				SizeRequested = size,
				SizeRead = bytes.Length,
				OutputPath = outputPath,
				Note = bytes.Length < size ? "Partial read — some pages may be inaccessible." : null
			}, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		// ── Helpers ──────────────────────────────────────────────────────────────

		// ── unpack_from_memory ───────────────────────────────────────────────────

		/// <summary>
		/// All-in-one unpack: launches the EXE under the debugger with BreakKind=EntryPoint
		/// (so the module .cctor/decryptor has already run), waits until paused, dumps the
		/// main module with PE-layout fix, and optionally stops the session.
		/// Arguments: exe_path* | output_path* | timeout_ms (default 30000) |
		///            stop_after_dump (default true) | module_name (auto-detected if omitted)
		/// </summary>
		public CallToolResult UnpackFromMemory(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("exe_path", out var exePathObj))
				throw new ArgumentException("exe_path is required");
			if (!arguments.TryGetValue("output_path", out var outputPathObj))
				throw new ArgumentException("output_path is required");

			var exePath    = exePathObj.ToString()    ?? string.Empty;
			var outputPath = outputPathObj.ToString() ?? string.Empty;

			if (!File.Exists(exePath))
				throw new ArgumentException($"File not found: {exePath}");
			if (string.IsNullOrWhiteSpace(outputPath))
				throw new ArgumentException("output_path must not be empty");

			int timeoutMs = 30000;
			if (arguments.TryGetValue("timeout_ms", out var toObj) && toObj is JsonElement toElem && toElem.TryGetInt32(out var toInt))
				timeoutMs = Math.Max(3000, toInt);

			bool stopAfterDump = true;
			if (arguments.TryGetValue("stop_after_dump", out var sadObj)) {
				if (sadObj is bool sadBool) stopAfterDump = sadBool;
				else if (sadObj is JsonElement sadElem) stopAfterDump = sadElem.ValueKind != JsonValueKind.False;
				else if (sadObj?.ToString()?.ToLowerInvariant() == "false") stopAfterDump = false;
			}

			string? moduleName = null;
			if (arguments.TryGetValue("module_name", out var mnObj))
				moduleName = mnObj?.ToString();

			var mgr = dbgManager.Value;
			var sw  = System.Diagnostics.Stopwatch.StartNew();

			// Launch under debugger if no session is active
			bool ownedSession = false;
			if (!mgr.IsDebugging) {
				var workDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
				var opts = new DotNetFrameworkStartDebuggingOptions {
					Filename         = exePath,
					WorkingDirectory = workDir,
					BreakKind        = PredefinedBreakKinds.EntryPoint,
				};
				var launchError = mgr.Start(opts);
				if (launchError != null)
					throw new InvalidOperationException($"Failed to launch debugger: {launchError}");
				ownedSession = true;
			}

			// Poll until the process pauses at the entry point
			bool hadProcess = false;
			var  deadline   = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (DateTime.UtcNow < deadline) {
				Thread.Sleep(200);
				var procs = mgr.Processes;
				if (procs.Length > 0 && mgr.IsRunning == false)
					break; // paused ✓
				if (procs.Length == 0 && hadProcess)
					throw new InvalidOperationException(
						"Process exited before reaching the entry point. " +
						"The target may have anti-debug protection or crashed on startup.");
				if (procs.Length > 0)
					hadProcess = true;
			}
			if (mgr.IsRunning != false || mgr.Processes.Length == 0)
				throw new TimeoutException(
					$"Timed out after {timeoutMs}ms waiting for the process to pause at entry point.");

			// Locate the target module
			var searchName = !string.IsNullOrEmpty(moduleName)
				? moduleName!
				: Path.GetFileName(exePath);
			var (targetModule, targetRuntime) = FindModule(mgr, searchName, null);

			if (targetModule == null) {
				// Fallback: find the sole exe module across all runtimes of the first process
				var exeMods = mgr.Processes[0].Runtimes
					.SelectMany(r => r.Modules.Select(m => (mod: m, rt: r)))
					.Where(t => t.mod.IsExe)
					.ToArray();
				if (exeMods.Length == 1) {
					targetModule  = exeMods[0].mod;
					targetRuntime = exeMods[0].rt;
				}
				else {
					var names = string.Join(", ", exeMods.Select(t => t.mod.Name));
					throw new ArgumentException(
						$"Module '{searchName}' not found. Loaded exe modules: {names}. " +
						"Pass module_name explicitly to disambiguate.");
				}
			}

			// Ensure output directory exists
			var outDir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(outDir))
				Directory.CreateDirectory(outDir);

			// Dump with the best available strategy
			var (dumpMethod, fileSize) = DumpModuleBytesToPath(targetModule, targetRuntime, outputPath);

			sw.Stop();

			// Optionally stop the debug session we started
			bool stopped = false;
			if (stopAfterDump && ownedSession) {
				try {
					mgr.StopDebuggingAll();
					stopped = true;
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, "StopDebuggingAll failed after unpack");
				}
			}

			var result = JsonSerializer.Serialize(new {
				ExePath       = exePath,
				OutputPath    = outputPath,
				ModuleName    = targetModule.Name,
				FileSizeBytes = fileSize,
				Method        = dumpMethod,
				ElapsedMs     = (int)sw.ElapsedMilliseconds,
				Stopped       = stopped
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		/// <summary>Dump a module to a file using the best available strategy. Returns (method, size).</summary>
		(string method, int size) DumpModuleBytesToPath(DbgModule module, DbgRuntime? runtime, string outputPath) {
			// Strategy A: IDbgDotNetRuntime.GetRawModuleBytes — highest-quality .NET dump
			if (runtime?.InternalRuntime is IDbgDotNetRuntime dotNetRuntime) {
				try {
					var rawData = dotNetRuntime.GetRawModuleBytes(module);
					if (rawData.RawBytes != null && rawData.RawBytes.Length > 0) {
						File.WriteAllBytes(outputPath, rawData.RawBytes);
						return ("IDbgDotNetRuntime.GetRawModuleBytes", rawData.RawBytes.Length);
					}
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, $"GetRawModuleBytes failed for {module.Name}, falling back to ReadMemory");
				}
			}

			// Strategy B: ReadMemory + optional PE layout fix
			if (!module.HasAddress)
				throw new InvalidOperationException(
					$"Module '{module.Name}' has no mapped address and GetRawModuleBytes returned nothing.");

			int moduleSize = (int)module.Size;
			if (moduleSize <= 0 || moduleSize > 512 * 1024 * 1024)
				throw new InvalidOperationException($"Module size out of safe range: {moduleSize:N0} bytes.");

			var bytes = module.Process.ReadMemory(module.Address, moduleSize);

			if (module.ImageLayout == DbgImageLayout.Memory) {
				var fixedBytes = TryConvertMemoryToFileLayout(bytes, out int fixedSize);
				if (fixedBytes != null) {
					File.WriteAllBytes(outputPath, fixedBytes.Take(fixedSize).ToArray());
					return ("ReadMemory+PELayoutFix", fixedSize);
				}
			}

			File.WriteAllBytes(outputPath, bytes);
			return ("ReadMemory", bytes.Length);
		}

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

		/// <summary>Helper: find a DbgModule by name across all processes/runtimes.</summary>
		(DbgModule? module, DbgRuntime? runtime) FindModule(DbgManager mgr, string moduleName, int? filterPid) {
			foreach (var process in mgr.Processes) {
				if (filterPid.HasValue && process.Id != filterPid.Value) continue;
				foreach (var runtime in process.Runtimes) {
					var found = runtime.Modules.FirstOrDefault(m =>
						(m.Name ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						Path.GetFileName(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase));
					if (found != null) return (found, runtime);
				}
			}
			return (null, null);
		}

		/// <summary>
		/// Attempts to convert a memory-layout PE dump to a file-layout PE.
		/// Copies section data from their virtual addresses to their raw file offsets.
		/// Returns null if conversion fails (malformed PE / no sections).
		/// </summary>
		static byte[]? TryConvertMemoryToFileLayout(byte[] memBytes, out int finalSize) {
			finalSize = 0;
			try {
				using var peImage = new dnlib.PE.PEImage(memBytes, dnlib.PE.ImageLayout.Memory, false);

				var sections = peImage.ImageSectionHeaders;
				if (sections == null || sections.Count == 0) return null;

				// Calculate required output size: max(PointerToRawData + SizeOfRawData)
				uint sizeOfHeaders = peImage.ImageNTHeaders?.OptionalHeader?.SizeOfHeaders ?? 0x400;
				uint maxRawEnd = sizeOfHeaders;
				foreach (var s in sections) {
					uint end = s.PointerToRawData + s.SizeOfRawData;
					if (end > maxRawEnd) maxRawEnd = end;
				}

				if (maxRawEnd == 0 || maxRawEnd > 512 * 1024 * 1024u) return null;

				var fileBytes = new byte[maxRawEnd];

				// Copy PE headers
				int hdrCopy = (int)Math.Min(sizeOfHeaders, (uint)memBytes.Length);
				Array.Copy(memBytes, 0, fileBytes, 0, hdrCopy);

				// Copy each section from its virtual address to its file offset
				foreach (var s in sections) {
					uint va = (uint)s.VirtualAddress;
					uint ptr = s.PointerToRawData;
					uint rawSz = s.SizeOfRawData;
					uint virtSz = s.VirtualSize;
					uint copyLen = Math.Min(rawSz, Math.Min(virtSz > 0 ? virtSz : rawSz, (uint)(memBytes.Length - (int)va)));

					if (va + copyLen > memBytes.Length || ptr + copyLen > fileBytes.Length) continue;
					Array.Copy(memBytes, (int)va, fileBytes, (int)ptr, (int)copyLen);
				}

				finalSize = (int)maxRawEnd;
				return fileBytes;
			}
			catch {
				return null;
			}
		}

		static string DescribeSectionCharacteristics(uint ch) {
			var parts = new List<string>();
			if ((ch & 0x00000020) != 0) parts.Add("CODE");
			if ((ch & 0x00000040) != 0) parts.Add("INITIALIZED_DATA");
			if ((ch & 0x00000080) != 0) parts.Add("UNINITIALIZED_DATA");
			if ((ch & 0x02000000) != 0) parts.Add("DISCARDABLE");
			if ((ch & 0x10000000) != 0) parts.Add("SHARED");
			if ((ch & 0x20000000) != 0) parts.Add("EXECUTE");
			if ((ch & 0x40000000) != 0) parts.Add("READ");
			if ((ch & 0x80000000u) != 0) parts.Add("WRITE");
			return parts.Count > 0 ? string.Join("|", parts) : $"0x{ch:X8}";
		}
	}
}
