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
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// Assembly editing operations: decompile type, change visibility, rename members, save assembly,
	/// list events, get custom attributes, and list nested types.
	/// Changes made here are in-memory only until save_assembly is called.
	/// </summary>
	[Export(typeof(EditTools))]
	public sealed class EditTools {
		readonly IDocumentTreeView documentTreeView;
		readonly IDecompilerService decompilerService;

		[ImportingConstructor]
		public EditTools(IDocumentTreeView documentTreeView, IDecompilerService decompilerService) {
			this.documentTreeView = documentTreeView;
			this.decompilerService = decompilerService;
		}

		/// <summary>
		/// Decompiles a full type to C# source code.
		/// Arguments: assembly_name, type_full_name
		/// </summary>
		public CallToolResult DecompileType(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			var decompiler = decompilerService.Decompiler;
			var output = new StringBuilderDecompilerOutput();
			var ctx = new DecompilationContext { CancellationToken = System.Threading.CancellationToken.None };
			decompiler.Decompile(type, output, ctx);

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = output.ToString() } }
			};
		}

		/// <summary>
		/// Changes the visibility/access modifier of a type member.
		/// Arguments: assembly_name, type_full_name, member_kind (type/method/field/property/event),
		///            member_name (ignored when member_kind="type"), new_visibility
		/// new_visibility values: public, private, protected, internal, protected_internal, private_protected
		/// When member_kind="type": changes the visibility of type_full_name itself.
		/// </summary>
		public CallToolResult ChangeVisibility(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");
			if (!arguments.TryGetValue("member_kind", out var memberKindObj))
				throw new ArgumentException("member_kind is required (type/method/field/property/event)");
			if (!arguments.TryGetValue("new_visibility", out var newVisObj))
				throw new ArgumentException("new_visibility is required (public/private/protected/internal/protected_internal/private_protected)");

			arguments.TryGetValue("member_name", out var memberNameObj);

			var assemblyName = asmNameObj.ToString() ?? "";
			var typeFullName = typeNameObj.ToString() ?? "";
			var memberKind = memberKindObj.ToString()?.ToLower() ?? "";
			var newVisibility = newVisObj.ToString()?.ToLower() ?? "";
			var memberName = memberNameObj?.ToString() ?? "";

			var assembly = FindAssemblyByName(assemblyName);
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {assemblyName}");

			var type = FindTypeInAssembly(assembly, typeFullName);
			if (type == null)
				throw new ArgumentException($"Type not found: {typeFullName}");

			string result;

			switch (memberKind) {
				case "type":
					// Change visibility of the type itself
					var newVis = type.IsNested
						? ParseNestedTypeVisibility(newVisibility)
						: ParseTopLevelTypeVisibility(newVisibility);
					type.Attributes = (type.Attributes & ~TypeAttributes.VisibilityMask) | newVis;
					result = $"Type '{type.FullName}' visibility changed to {newVisibility}";
					break;

				case "method": {
					var method = type.Methods.FirstOrDefault(m => m.Name.String == memberName);
					if (method == null)
						throw new ArgumentException($"Method '{memberName}' not found in {typeFullName}");
					method.Access = ParseMethodAccess(newVisibility);
					result = $"Method '{memberName}' visibility changed to {newVisibility}";
					break;
				}

				case "field": {
					var field = type.Fields.FirstOrDefault(f => f.Name.String == memberName);
					if (field == null)
						throw new ArgumentException($"Field '{memberName}' not found in {typeFullName}");
					field.Access = ParseFieldAccess(newVisibility);
					result = $"Field '{memberName}' visibility changed to {newVisibility}";
					break;
				}

				case "property": {
					var prop = type.Properties.FirstOrDefault(p => p.Name.String == memberName);
					if (prop == null)
						throw new ArgumentException($"Property '{memberName}' not found in {typeFullName}");
					var access = ParseMethodAccess(newVisibility);
					if (prop.GetMethod != null) prop.GetMethod.Access = access;
					if (prop.SetMethod != null) prop.SetMethod.Access = access;
					result = $"Property '{memberName}' accessor visibility changed to {newVisibility}";
					break;
				}

				case "event": {
					var ev = type.Events.FirstOrDefault(e => e.Name.String == memberName);
					if (ev == null)
						throw new ArgumentException($"Event '{memberName}' not found in {typeFullName}");
					var access = ParseMethodAccess(newVisibility);
					if (ev.AddMethod != null) ev.AddMethod.Access = access;
					if (ev.RemoveMethod != null) ev.RemoveMethod.Access = access;
					if (ev.InvokeMethod != null) ev.InvokeMethod.Access = access;
					result = $"Event '{memberName}' accessor visibility changed to {newVisibility}";
					break;
				}

				default:
					throw new ArgumentException($"Invalid member_kind: '{memberKind}'. Expected: type/method/field/property/event");
			}

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result + "\nNote: Changes are in-memory. Use save_assembly to persist to disk." } }
			};
		}

		/// <summary>
		/// Renames a type member.
		/// Arguments: assembly_name, type_full_name, member_kind (type/method/field/property/event),
		///            old_name, new_name
		/// When member_kind="type": renames the type itself (old_name must match its simple Name).
		/// </summary>
		public CallToolResult RenameMember(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");
			if (!arguments.TryGetValue("member_kind", out var memberKindObj))
				throw new ArgumentException("member_kind is required (type/method/field/property/event)");
			if (!arguments.TryGetValue("old_name", out var oldNameObj))
				throw new ArgumentException("old_name is required");
			if (!arguments.TryGetValue("new_name", out var newNameObj))
				throw new ArgumentException("new_name is required");

			var assemblyName = asmNameObj.ToString() ?? "";
			var typeFullName = typeNameObj.ToString() ?? "";
			var memberKind = memberKindObj.ToString()?.ToLower() ?? "";
			var oldName = oldNameObj.ToString() ?? "";
			var newName = newNameObj.ToString() ?? "";

			if (string.IsNullOrWhiteSpace(newName))
				throw new ArgumentException("new_name cannot be empty");

			var assembly = FindAssemblyByName(assemblyName);
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {assemblyName}");

			var type = FindTypeInAssembly(assembly, typeFullName);
			if (type == null)
				throw new ArgumentException($"Type not found: {typeFullName}");

			string result;

			switch (memberKind) {
				case "type":
					if (type.Name.String != oldName)
						throw new ArgumentException($"Type name '{oldName}' does not match actual type name '{type.Name.String}'");
					type.Name = newName;
					result = $"Type '{oldName}' renamed to '{newName}'";
					break;

				case "method": {
					var method = type.Methods.FirstOrDefault(m => m.Name.String == oldName);
					if (method == null)
						throw new ArgumentException($"Method '{oldName}' not found in {typeFullName}");
					method.Name = newName;
					result = $"Method '{oldName}' renamed to '{newName}'";
					break;
				}

				case "field": {
					var field = type.Fields.FirstOrDefault(f => f.Name.String == oldName);
					if (field == null)
						throw new ArgumentException($"Field '{oldName}' not found in {typeFullName}");
					field.Name = newName;
					result = $"Field '{oldName}' renamed to '{newName}'";
					break;
				}

				case "property": {
					var prop = type.Properties.FirstOrDefault(p => p.Name.String == oldName);
					if (prop == null)
						throw new ArgumentException($"Property '{oldName}' not found in {typeFullName}");
					prop.Name = newName;
					result = $"Property '{oldName}' renamed to '{newName}'";
					break;
				}

				case "event": {
					var ev = type.Events.FirstOrDefault(e => e.Name.String == oldName);
					if (ev == null)
						throw new ArgumentException($"Event '{oldName}' not found in {typeFullName}");
					ev.Name = newName;
					result = $"Event '{oldName}' renamed to '{newName}'";
					break;
				}

				default:
					throw new ArgumentException($"Invalid member_kind: '{memberKind}'. Expected: type/method/field/property/event");
			}

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result + "\nNote: Changes are in-memory. Use save_assembly to persist to disk." } }
			};
		}

		/// <summary>
		/// Saves a modified assembly to disk using dnlib's module writer.
		/// Arguments: assembly_name, output_path (optional; defaults to original file location)
		/// </summary>
		public CallToolResult SaveAssembly(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");

			var assemblyName = asmNameObj.ToString() ?? "";
			var assembly = FindAssemblyByName(assemblyName);
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {assemblyName}");

			string? outputPath = null;
			if (arguments.TryGetValue("output_path", out var outputPathObj))
				outputPath = outputPathObj?.ToString();

			var module = assembly.ManifestModule;
			var savePath = !string.IsNullOrEmpty(outputPath) ? outputPath! : module.Location;

			if (string.IsNullOrEmpty(savePath))
				throw new ArgumentException("Cannot determine output path. Module has no file location. Provide output_path explicitly.");

			try {
				if (module.IsILOnly) {
					var writerOptions = new ModuleWriterOptions(module);
					module.Write(savePath, writerOptions);
				}
				else if (module is ModuleDefMD moduleDefMD) {
					var writerOptions = new NativeModuleWriterOptions(moduleDefMD, optimizeImageSize: false);
					moduleDefMD.NativeWrite(savePath, writerOptions);
				}
				else {
					// Fallback: force IL-only write for dynamic/in-memory modules
					var writerOptions = new ModuleWriterOptions(module);
					module.Write(savePath, writerOptions);
				}

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"Assembly '{assembly.Name}' saved successfully to: {savePath}" } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to save assembly '{assemblyName}': {ex.Message}");
			}
		}

		/// <summary>
		/// Lists all events defined in a type.
		/// Arguments: assembly_name, type_full_name
		/// </summary>
		public CallToolResult ListEventsInType(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			var events = type.Events.Select(e => new {
				Name = e.Name.String,
				EventType = e.EventType?.FullName ?? "Unknown",
				HasAddMethod = e.AddMethod != null,
				HasRemoveMethod = e.RemoveMethod != null,
				HasInvokeMethod = e.InvokeMethod != null,
				IsPublic = e.AddMethod?.IsPublic ?? false,
				IsStatic = e.AddMethod?.IsStatic ?? false,
				AddMethodName = e.AddMethod?.Name.String,
				RemoveMethodName = e.RemoveMethod?.Name.String,
				InvokeMethodName = e.InvokeMethod?.Name.String,
				CustomAttributes = e.CustomAttributes.Select(ca => ca.AttributeType.FullName).ToList()
			}).ToList();

			var result = JsonSerializer.Serialize(new {
				Type = type.FullName,
				EventCount = events.Count,
				Events = events
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		/// <summary>
		/// Gets custom attributes on a type or one of its members.
		/// Arguments: assembly_name, type_full_name,
		///            member_name (optional), member_kind (optional: method/field/property/event)
		/// If member_name is omitted, returns attributes on the type itself.
		/// </summary>
		public CallToolResult GetCustomAttributes(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			string? memberName = null;
			if (arguments.TryGetValue("member_name", out var memberNameObj))
				memberName = memberNameObj?.ToString();

			string? memberKind = null;
			if (arguments.TryGetValue("member_kind", out var memberKindObj))
				memberKind = memberKindObj?.ToString()?.ToLower();

			List<object> attrs;
			string targetDescription;

			if (string.IsNullOrEmpty(memberName)) {
				attrs = ExtractCustomAttributes(type.CustomAttributes);
				targetDescription = type.FullName;
			}
			else {
				switch (memberKind) {
					case "method": {
						var m = type.Methods.FirstOrDefault(x => x.Name.String == memberName);
						if (m == null) throw new ArgumentException($"Method not found: {memberName}");
						attrs = ExtractCustomAttributes(m.CustomAttributes);
						targetDescription = $"{type.FullName}.{memberName}()";
						break;
					}
					case "field": {
						var f = type.Fields.FirstOrDefault(x => x.Name.String == memberName);
						if (f == null) throw new ArgumentException($"Field not found: {memberName}");
						attrs = ExtractCustomAttributes(f.CustomAttributes);
						targetDescription = $"{type.FullName}.{memberName}";
						break;
					}
					case "property": {
						var p = type.Properties.FirstOrDefault(x => x.Name.String == memberName);
						if (p == null) throw new ArgumentException($"Property not found: {memberName}");
						attrs = ExtractCustomAttributes(p.CustomAttributes);
						targetDescription = $"{type.FullName}.{memberName}";
						break;
					}
					case "event": {
						var e = type.Events.FirstOrDefault(x => x.Name.String == memberName);
						if (e == null) throw new ArgumentException($"Event not found: {memberName}");
						attrs = ExtractCustomAttributes(e.CustomAttributes);
						targetDescription = $"{type.FullName}.{memberName}";
						break;
					}
					default: {
						// Try auto-detect: search in methods, fields, properties, events
						var anyMethod = type.Methods.FirstOrDefault(x => x.Name.String == memberName);
						if (anyMethod != null) { attrs = ExtractCustomAttributes(anyMethod.CustomAttributes); targetDescription = $"{type.FullName}.{memberName}()"; break; }
						var anyField = type.Fields.FirstOrDefault(x => x.Name.String == memberName);
						if (anyField != null) { attrs = ExtractCustomAttributes(anyField.CustomAttributes); targetDescription = $"{type.FullName}.{memberName}"; break; }
						var anyProp = type.Properties.FirstOrDefault(x => x.Name.String == memberName);
						if (anyProp != null) { attrs = ExtractCustomAttributes(anyProp.CustomAttributes); targetDescription = $"{type.FullName}.{memberName}"; break; }
						var anyEvent = type.Events.FirstOrDefault(x => x.Name.String == memberName);
						if (anyEvent != null) { attrs = ExtractCustomAttributes(anyEvent.CustomAttributes); targetDescription = $"{type.FullName}.{memberName}"; break; }
						throw new ArgumentException($"Member '{memberName}' not found in {type.FullName}. Specify member_kind to disambiguate.");
					}
				}
			}

			var result = JsonSerializer.Serialize(new {
				Target = targetDescription,
				AttributeCount = attrs.Count,
				CustomAttributes = attrs
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		/// <summary>
		/// Lists all nested types inside a type, recursively.
		/// Arguments: assembly_name, type_full_name
		/// </summary>
		public CallToolResult ListNestedTypes(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			var nested = GetNestedTypesRecursive(type).Select(t => new {
				FullName = t.FullName,
				Name = t.Name.String,
				IsPublic = t.IsNestedPublic,
				IsPrivate = t.IsNestedPrivate,
				IsProtected = t.IsNestedFamily,
				IsInternal = t.IsNestedAssembly,
				IsClass = t.IsClass,
				IsInterface = t.IsInterface,
				IsEnum = t.IsEnum,
				IsValueType = t.IsValueType,
				IsAbstract = t.IsAbstract,
				IsSealed = t.IsSealed,
				MethodCount = t.Methods.Count,
				FieldCount = t.Fields.Count
			}).ToList();

			var result = JsonSerializer.Serialize(new {
				ContainingType = type.FullName,
				NestedTypeCount = nested.Count,
				NestedTypes = nested
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		// ── Assembly Metadata ───────────────────────────────────────────────────

	/// <summary>
	/// Returns all metadata fields of an assembly.
	/// Arguments: assembly_name
	/// </summary>
	public CallToolResult GetAssemblyMetadata(Dictionary<string, object>? arguments) {
		if (arguments == null)
			throw new ArgumentException("Arguments required");
		if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
			throw new ArgumentException("assembly_name is required");

		var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
		if (assembly == null)
			throw new ArgumentException($"Assembly not found: {asmNameObj}");

		var module = assembly.ManifestModule;
		var flags = assembly.Attributes;

		var metadata = new {
			Name = assembly.Name.String,
			Version = assembly.Version?.ToString() ?? "0.0.0.0",
			Culture = assembly.Culture ?? "",
			PublicKey = assembly.HasPublicKey
				? BitConverter.ToString(assembly.PublicKey.Data).Replace("-", "").ToLower()
				: "",
			PublicKeyToken = assembly.PublicKeyToken != null
				? BitConverter.ToString(assembly.PublicKeyToken.Data).Replace("-", "").ToLower()
				: "",
			HashAlgorithm = assembly.HashAlgorithm.ToString(),
			Flags = flags.ToString(),
			FlagsRaw = (uint)flags,
			HasPublicKey = assembly.HasPublicKey,
			IsRetargetable = (flags & AssemblyAttributes.Retargetable) != 0,
			DisableJITOptimizer = (flags & AssemblyAttributes.DisableJITcompileOptimizer) != 0,
			EnableJITTracking = (flags & AssemblyAttributes.EnableJITcompileTracking) != 0,
			ProcessorArchitecture = (flags & AssemblyAttributes.PA_FullMask) switch {
				AssemblyAttributes.PA_x86    => "x86",
				AssemblyAttributes.PA_AMD64  => "AMD64",
				AssemblyAttributes.PA_IA64   => "IA64",
				AssemblyAttributes.PA_ARM    => "ARM",
				_                            => "AnyCPU"
			},
			ContentType = (flags & AssemblyAttributes.ContentType_Mask) == AssemblyAttributes.ContentType_WindowsRuntime
				? "WindowsRuntime" : "Default",
			Location = module?.Location ?? "",
			ModuleCount = assembly.Modules.Count,
			CustomAttributes = assembly.CustomAttributes
				.Select(ca => new {
					Type = ca.AttributeType?.FullName ?? "Unknown",
					Args = ca.ConstructorArguments.Select(a => a.Value?.ToString() ?? "null").ToList()
				}).ToList()
		};

		var result = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = result } }
		};
	}

	/// <summary>
	/// Edits assembly-level metadata fields.
	/// Arguments: assembly_name, new_name (opt), version (opt, "1.2.3.4"), culture (opt), hash_algorithm (opt: SHA1/MD5/None)
	/// Changes are in-memory until save_assembly is called.
	/// </summary>
	public CallToolResult EditAssemblyMetadata(Dictionary<string, object>? arguments) {
		if (arguments == null)
			throw new ArgumentException("Arguments required");
		if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
			throw new ArgumentException("assembly_name is required");

		var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
		if (assembly == null)
			throw new ArgumentException($"Assembly not found: {asmNameObj}");

		var changes = new List<string>();

		if (arguments.TryGetValue("new_name", out var newNameObj) && newNameObj?.ToString() is string newName && newName.Length > 0) {
			assembly.Name = newName;
			changes.Add($"Name → {newName}");
		}

		if (arguments.TryGetValue("version", out var versionObj) && versionObj?.ToString() is string versionStr) {
			if (!Version.TryParse(versionStr, out var newVersion))
				throw new ArgumentException($"Invalid version format: '{versionStr}'. Expected: major.minor.build.revision");
			assembly.Version = newVersion;
			changes.Add($"Version → {newVersion}");
		}

		if (arguments.TryGetValue("culture", out var cultureObj)) {
			assembly.Culture = cultureObj?.ToString() ?? "";
			changes.Add($"Culture → '{assembly.Culture}'");
		}

		if (arguments.TryGetValue("hash_algorithm", out var hashObj) && hashObj?.ToString() is string hashStr) {
			assembly.HashAlgorithm = hashStr.ToUpperInvariant() switch {
				"SHA1" => AssemblyHashAlgorithm.SHA1,
				"MD5"  => AssemblyHashAlgorithm.MD5,
				"NONE" or "0" => AssemblyHashAlgorithm.None,
				_ => throw new ArgumentException($"Invalid hash_algorithm: '{hashStr}'. Use: SHA1, MD5, None")
			};
			changes.Add($"HashAlgorithm → {assembly.HashAlgorithm}");
		}

		if (changes.Count == 0)
			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = "No changes specified. Provide at least one of: new_name, version, culture, hash_algorithm." } }
			};

		var summary = string.Join("\n", changes.Select(c => "  • " + c));
		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent {
				Text = $"Assembly '{asmNameObj}' metadata updated:\n{summary}\nNote: Use save_assembly to persist to disk."
			} }
		};
	}

	/// <summary>
	/// Sets or clears an individual assembly attribute flag.
	/// Arguments: assembly_name, flag_name, value (bool)
	/// flag_name values: PublicKey | Retargetable | DisableJITOptimizer | EnableJITTracking | WindowsRuntime
	///   ProcessorArchitecture: MSIL | x86 | AMD64 | ARM | ARM64 | IA64 | AnyCPU
	/// </summary>
	public CallToolResult SetAssemblyFlags(Dictionary<string, object>? arguments) {
		if (arguments == null)
			throw new ArgumentException("Arguments required");
		if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
			throw new ArgumentException("assembly_name is required");
		if (!arguments.TryGetValue("flag_name", out var flagNameObj))
			throw new ArgumentException("flag_name is required");
		if (!arguments.TryGetValue("value", out var valueObj))
			throw new ArgumentException("value is required (true/false, or architecture name for ProcessorArchitecture)");

		var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
		if (assembly == null)
			throw new ArgumentException($"Assembly not found: {asmNameObj}");

		var flagName = flagNameObj.ToString()?.ToLowerInvariant() ?? "";
		var valueStr = valueObj.ToString() ?? "";
		var current = assembly.Attributes;

		string change;
		switch (flagName) {
			case "publickey":
				var boolPK = ParseBool(valueStr, flagName);
				assembly.Attributes = boolPK ? (current | AssemblyAttributes.PublicKey) : (current & ~AssemblyAttributes.PublicKey);
				change = $"PublicKey = {boolPK}";
				break;
			case "retargetable":
				var boolR = ParseBool(valueStr, flagName);
				assembly.Attributes = boolR ? (current | AssemblyAttributes.Retargetable) : (current & ~AssemblyAttributes.Retargetable);
				change = $"Retargetable = {boolR}";
				break;
			case "disablejitoptimizer":
				var boolDJ = ParseBool(valueStr, flagName);
				assembly.Attributes = boolDJ ? (current | AssemblyAttributes.DisableJITcompileOptimizer) : (current & ~AssemblyAttributes.DisableJITcompileOptimizer);
				change = $"DisableJITOptimizer = {boolDJ}";
				break;
			case "enablejittracking":
				var boolEJ = ParseBool(valueStr, flagName);
				assembly.Attributes = boolEJ ? (current | AssemblyAttributes.EnableJITcompileTracking) : (current & ~AssemblyAttributes.EnableJITcompileTracking);
				change = $"EnableJITTracking = {boolEJ}";
				break;
			case "windowsruntime":
				var boolWR = ParseBool(valueStr, flagName);
				assembly.Attributes = (current & ~AssemblyAttributes.ContentType_Mask) |
					(boolWR ? AssemblyAttributes.ContentType_WindowsRuntime : AssemblyAttributes.ContentType_Default);
				change = $"ContentType = {(boolWR ? "WindowsRuntime" : "Default")}";
				break;
			case "processorarchitecture":
				var arch = valueStr.ToUpperInvariant() switch {
					"ANYCPU" or "NONE" or "MSIL" => AssemblyAttributes.PA_MSIL,
					"X86" or "I386"              => AssemblyAttributes.PA_x86,
					"AMD64" or "X64"             => AssemblyAttributes.PA_AMD64,
					"ARM"                        => AssemblyAttributes.PA_ARM,
					"ARM64"                      => AssemblyAttributes.PA_ARM64,
					"IA64"                       => AssemblyAttributes.PA_IA64,
					_ => throw new ArgumentException($"Unknown architecture '{valueStr}'. Use: AnyCPU, x86, AMD64, ARM, ARM64, IA64")
				};
				assembly.Attributes = (current & ~AssemblyAttributes.PA_FullMask) | arch | AssemblyAttributes.PA_Specified;
				change = $"ProcessorArchitecture = {valueStr}";
				break;
			default:
				throw new ArgumentException(
					$"Unknown flag_name: '{flagName}'. Valid: PublicKey, Retargetable, DisableJITOptimizer, EnableJITTracking, WindowsRuntime, ProcessorArchitecture");
		}

		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent {
				Text = $"Flag updated on '{asmNameObj}': {change}\nNew flags: {assembly.Attributes}\nNote: Use save_assembly to persist."
			} }
		};
	}

	static bool ParseBool(string s, string paramName) =>
		s.ToLowerInvariant() switch {
			"true" or "1" or "yes" => true,
			"false" or "0" or "no" => false,
			_ => throw new ArgumentException($"'{paramName}' value must be true/false, got: '{s}'")
		};

	// ── Assembly References ──────────────────────────────────────────────────

	/// <summary>
	/// Lists all assembly references in a module's manifest.
	/// Arguments: assembly_name
	/// </summary>
	public CallToolResult ListAssemblyReferences(Dictionary<string, object>? arguments) {
		if (arguments == null)
			throw new ArgumentException("Arguments required");
		if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
			throw new ArgumentException("assembly_name is required");

		var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
		if (assembly == null)
			throw new ArgumentException($"Assembly not found: {asmNameObj}");

		var module = assembly.ManifestModule;
		var refs = module.GetAssemblyRefs().Select((r, i) => new {
			Index = i,
			Name = r.Name.String,
			Version = r.Version?.ToString() ?? "0.0.0.0",
			Culture = r.Culture ?? "",
			PublicKeyToken = r.PublicKeyOrToken?.Data != null && r.PublicKeyOrToken.Data.Length > 0
				? BitConverter.ToString(r.PublicKeyOrToken.Data).Replace("-", "").ToLower()
				: "",
			IsRetargetable = r.IsRetargetable,
			IsWindowsRuntime = (r.Attributes & AssemblyAttributes.ContentType_WindowsRuntime) != 0,
			FullName = r.FullName
		}).ToList();

		var result = JsonSerializer.Serialize(new {
			Assembly = assembly.Name.String,
			ReferenceCount = refs.Count,
			References = refs
		}, new JsonSerializerOptions { WriteIndented = true });

		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = result } }
		};
	}

	/// <summary>
	/// Adds an assembly reference by loading a DLL from disk.
	/// Creates an AssemblyRef entry and a TypeForwarder to anchor it in the manifest.
	/// Arguments: assembly_name, dll_path, type_name (opt: specific type to forward, defaults to first public type)
	/// Changes are in-memory until save_assembly is called.
	/// </summary>
	public CallToolResult AddAssemblyReference(Dictionary<string, object>? arguments) {
		if (arguments == null)
			throw new ArgumentException("Arguments required");
		if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
			throw new ArgumentException("assembly_name is required");
		if (!arguments.TryGetValue("dll_path", out var dllPathObj))
			throw new ArgumentException("dll_path is required");

		var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
		if (assembly == null)
			throw new ArgumentException($"Assembly not found: {asmNameObj}");

		var dllPath = dllPathObj.ToString() ?? "";
		if (!System.IO.File.Exists(dllPath))
			throw new ArgumentException($"DLL not found at path: {dllPath}");

		var targetModule = assembly.ManifestModule;

		using var srcModule = ModuleDefMD.Load(dllPath);
		var srcAssembly = srcModule.Assembly
			?? throw new ArgumentException("Source file is not an assembly (no manifest): " + dllPath);

		// Build the AssemblyRef
		var asmRef = new AssemblyRefUser(
			srcAssembly.Name,
			srcAssembly.Version,
			srcAssembly.PublicKeyToken,
			srcAssembly.Culture);

		// Find anchor type to create a TypeForwarder (ensures AssemblyRef is serialized)
		string? anchorTypeName = null;
		string? anchorTypeNamespace = null;
		if (arguments.TryGetValue("type_name", out var typeNameObj) && typeNameObj?.ToString() is string tn && tn.Length > 0) {
			var src = srcModule.Types.FirstOrDefault(t => t.FullName == tn || t.Name.String == tn)
				?? throw new ArgumentException($"Type '{tn}' not found in source DLL");
			anchorTypeName = src.Name.String;
			anchorTypeNamespace = src.Namespace.String;
		}
		else {
			var firstPublic = srcModule.Types.FirstOrDefault(t => t.IsPublic);
			if (firstPublic != null) {
				anchorTypeName = firstPublic.Name.String;
				anchorTypeNamespace = firstPublic.Namespace.String;
			}
		}

		string typeForwardInfo;
		if (anchorTypeName != null) {
			// TypeForwarder: declares this assembly re-exports the type from the referenced assembly
			var exported = new ExportedTypeUser(targetModule, 0,
				new UTF8String(anchorTypeNamespace ?? ""), new UTF8String(anchorTypeName),
				TypeAttributes.Public, asmRef);
			targetModule.ExportedTypes.Add(exported);
			typeForwardInfo = $"TypeForwarder added: {anchorTypeNamespace}.{anchorTypeName} → {srcAssembly.Name}";
		}
		else {
			// No public types — create a minimal TypeRef on the <Module> type so AssemblyRef is referenced
			var modType = targetModule.GlobalType;
			var typeRef = new TypeRefUser(targetModule, "Object", "System", asmRef);
			// Add as a custom attribute constructor ref (we use a dummy CA to carry the reference)
			typeForwardInfo = "No public types in source DLL; AssemblyRef created without TypeForwarder (may not persist without an explicit TypeRef usage).";
		}

		var result = JsonSerializer.Serialize(new {
			Added = srcAssembly.FullName,
			SourcePath = dllPath,
			TypeForwarder = typeForwardInfo,
			Note = "Call save_assembly to write changes to disk."
		}, new JsonSerializerOptions { WriteIndented = true });

		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = result } }
		};
	}

	/// <summary>
	/// Deep-clones a type from an external DLL file into the target assembly.
	/// Copies fields, methods (with IL body), properties, and events.
	/// Arguments: assembly_name, dll_path, source_type, target_namespace (opt), overwrite (opt bool)
	/// Changes are in-memory until save_assembly is called.
	/// </summary>
	public CallToolResult InjectTypeFromDll(Dictionary<string, object>? arguments) {
		if (arguments == null)
			throw new ArgumentException("Arguments required");
		if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
			throw new ArgumentException("assembly_name is required");
		if (!arguments.TryGetValue("dll_path", out var dllPathObj))
			throw new ArgumentException("dll_path is required");
		if (!arguments.TryGetValue("source_type", out var srcTypeObj))
			throw new ArgumentException("source_type is required (full name of type to inject)");

		var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
		if (assembly == null)
			throw new ArgumentException($"Assembly not found: {asmNameObj}");

		var dllPath = dllPathObj.ToString() ?? "";
		if (!System.IO.File.Exists(dllPath))
			throw new ArgumentException($"DLL not found: {dllPath}");

		var sourceTypeName = srcTypeObj.ToString() ?? "";
		var targetModule = assembly.ManifestModule;

		string? targetNamespace = null;
		if (arguments.TryGetValue("target_namespace", out var nsObj))
			targetNamespace = nsObj?.ToString();

		bool overwrite = false;
		if (arguments.TryGetValue("overwrite", out var owObj)) {
			if (owObj is bool owBool)
				overwrite = owBool;
			else if (owObj is System.Text.Json.JsonElement owElem)
				overwrite = owElem.ValueKind == System.Text.Json.JsonValueKind.True;
			else if (owObj?.ToString()?.ToLowerInvariant() == "true")
				overwrite = true;
		}

		using var srcModule = ModuleDefMD.Load(dllPath);
		var srcType = srcModule.Types.FirstOrDefault(t =>
			t.FullName == sourceTypeName || t.Name.String == sourceTypeName)
			?? throw new ArgumentException($"Type '{sourceTypeName}' not found in {dllPath}");

		var finalNs = targetNamespace ?? srcType.Namespace.String;
		var finalName = srcType.Name.String;

		// Check for existing type
		var existing = targetModule.Types.FirstOrDefault(t =>
			t.Name.String == finalName && t.Namespace.String == finalNs);
		if (existing != null) {
			if (!overwrite)
				throw new ArgumentException(
					$"Type '{finalNs}.{finalName}' already exists. Set overwrite=true to replace it.");
			targetModule.Types.Remove(existing);
		}

		// Importer remaps type references from srcModule → targetModule
		var importer = new Importer(targetModule, ImporterOptions.TryToUseDefs);

		// ── Create the new TypeDef ──
		var newType = new TypeDefUser(finalNs, finalName,
			srcType.BaseType != null ? importer.Import(srcType.BaseType) : null) {
			Attributes = srcType.Attributes
		};

		// Generic parameters
		foreach (var gp in srcType.GenericParameters) {
			var ngp = new GenericParamUser(gp.Number, gp.Flags, gp.Name);
			foreach (var c in gp.GenericParamConstraints)
				ngp.GenericParamConstraints.Add(new GenericParamConstraintUser(importer.Import(c.Constraint)));
			newType.GenericParameters.Add(ngp);
		}

		// Interfaces
		foreach (var iface in srcType.Interfaces)
			newType.Interfaces.Add(new InterfaceImplUser(importer.Import(iface.Interface)));

		// Fields
		foreach (var srcField in srcType.Fields) {
			var newField = new FieldDefUser(srcField.Name,
				(FieldSig)importer.Import(srcField.FieldSig)!,
				srcField.Attributes);
			if (srcField.HasConstant && srcField.Constant != null)
				newField.Constant = new ConstantUser(srcField.Constant.Value, srcField.Constant.Type);
			newType.Fields.Add(newField);
		}

		// Methods (with IL)
		foreach (var srcMethod in srcType.Methods) {
			var newMethod = new MethodDefUser(srcMethod.Name,
				(MethodSig)importer.Import(srcMethod.MethodSig)!,
				srcMethod.ImplAttributes,
				srcMethod.Attributes);

			// Generic parameters
			foreach (var gp in srcMethod.GenericParameters) {
				var ngp = new GenericParamUser(gp.Number, gp.Flags, gp.Name);
				newMethod.GenericParameters.Add(ngp);
			}

			// Parameters (for names/defaults)
			foreach (var p in srcMethod.ParamDefs) {
				var np = new ParamDefUser(p.Name, p.Sequence, p.Attributes);
				newMethod.ParamDefs.Add(np);
			}

			// IL body
			if (srcMethod.HasBody && srcMethod.Body != null) {
				var srcBody = srcMethod.Body;
				var newBody = new dnlib.DotNet.Emit.CilBody {
					MaxStack = srcBody.MaxStack,
					InitLocals = srcBody.InitLocals,
					KeepOldMaxStack = srcBody.KeepOldMaxStack,
				};

				// Locals
				var localMap = new Dictionary<dnlib.DotNet.Emit.Local, dnlib.DotNet.Emit.Local>();
				foreach (var loc in srcBody.Variables) {
					var nl = new dnlib.DotNet.Emit.Local(importer.Import(loc.Type)) { Name = loc.Name };
					newBody.Variables.Add(nl);
					localMap[loc] = nl;
				}

				// Instructions — first pass: create stubs (needed for branch target mapping)
				var instrMap = new Dictionary<dnlib.DotNet.Emit.Instruction, dnlib.DotNet.Emit.Instruction>();
				foreach (var srcInstr in srcBody.Instructions) {
					var newInstr = new dnlib.DotNet.Emit.Instruction(srcInstr.OpCode);
					instrMap[srcInstr] = newInstr;
					newBody.Instructions.Add(newInstr);
				}

				// Instructions — second pass: fix operands
				for (int i = 0; i < srcBody.Instructions.Count; i++) {
					var si = srcBody.Instructions[i];
					var ni = newBody.Instructions[i];
					ni.Operand = si.Operand switch {
						dnlib.DotNet.Emit.Instruction target
							=> instrMap.TryGetValue(target, out var mapped) ? mapped : null,
						dnlib.DotNet.Emit.Instruction[] targets
							=> targets.Select(t => instrMap.TryGetValue(t, out var m) ? m : null)
							          .Where(x => x != null).ToArray(),
						ITypeDefOrRef tdr    => importer.Import(tdr),
						IField        fld    => importer.Import(fld),
						IMethod       mth    => importer.Import(mth),
							dnlib.DotNet.Emit.Local loc
							=> localMap.TryGetValue(loc, out var ml) ? ml : (object?)loc,
						_ => si.Operand  // string, int, float, sbyte, long, double, etc.
					};
				}

				// Exception handlers
				foreach (var eh in srcBody.ExceptionHandlers) {
					newBody.ExceptionHandlers.Add(new dnlib.DotNet.Emit.ExceptionHandler(eh.HandlerType) {
						TryStart    = eh.TryStart    != null && instrMap.TryGetValue(eh.TryStart, out var ts)  ? ts  : null,
						TryEnd      = eh.TryEnd      != null && instrMap.TryGetValue(eh.TryEnd,   out var te)  ? te  : null,
						HandlerStart= eh.HandlerStart!= null && instrMap.TryGetValue(eh.HandlerStart, out var hs) ? hs : null,
						HandlerEnd  = eh.HandlerEnd  != null && instrMap.TryGetValue(eh.HandlerEnd,   out var he) ? he : null,
						FilterStart = eh.FilterStart != null && instrMap.TryGetValue(eh.FilterStart,  out var fs) ? fs : null,
						CatchType   = eh.CatchType   != null ? importer.Import(eh.CatchType) : null,
					});
				}

				newMethod.Body = newBody;
			}

			newType.Methods.Add(newMethod);
		}

		// Properties
		foreach (var srcProp in srcType.Properties) {
			var newProp = new PropertyDefUser(srcProp.Name,
				(PropertySig)importer.Import(srcProp.PropertySig)!,
				srcProp.Attributes);
			if (srcProp.GetMethod != null)
				newProp.GetMethod = newType.Methods.FirstOrDefault(m => m.Name.String == srcProp.GetMethod.Name.String);
			if (srcProp.SetMethod != null)
				newProp.SetMethod = newType.Methods.FirstOrDefault(m => m.Name.String == srcProp.SetMethod.Name.String);
			newType.Properties.Add(newProp);
		}

		// Events
		foreach (var srcEvent in srcType.Events) {
			var newEvent = new EventDefUser(srcEvent.Name,
				srcEvent.EventType != null ? importer.Import(srcEvent.EventType) : null,
				srcEvent.Attributes);
			if (srcEvent.AddMethod != null)
				newEvent.AddMethod = newType.Methods.FirstOrDefault(m => m.Name.String == srcEvent.AddMethod.Name.String);
			if (srcEvent.RemoveMethod != null)
				newEvent.RemoveMethod = newType.Methods.FirstOrDefault(m => m.Name.String == srcEvent.RemoveMethod.Name.String);
			if (srcEvent.InvokeMethod != null)
				newEvent.InvokeMethod = newType.Methods.FirstOrDefault(m => m.Name.String == srcEvent.InvokeMethod.Name.String);
			newType.Events.Add(newEvent);
		}

		targetModule.Types.Add(newType);

		var stats = new {
			InjectedType  = $"{finalNs}.{finalName}",
			SourceDll     = System.IO.Path.GetFileName(dllPath),
			SourceType    = srcType.FullName,
			Overwritten   = existing != null,
			Fields        = newType.Fields.Count,
			Methods       = newType.Methods.Count,
			Properties    = newType.Properties.Count,
			Events        = newType.Events.Count,
			Interfaces    = newType.Interfaces.Count,
			Note          = "Call save_assembly to persist to disk."
		};
		var result = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = result } }
		};
	}

	// ── Method Patching ──────────────────────────────────────────────────────

	/// <summary>
	/// Lists all P/Invoke (DllImport) declarations in a type.
	/// Returns the managed method name, token, DLL name, and native function name.
	/// Arguments: assembly_name, type_full_name
	/// </summary>
	public CallToolResult ListPInvokeMethods(Dictionary<string, object>? arguments) {
		if (arguments == null)
			throw new ArgumentException("Arguments required");
		if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
			throw new ArgumentException("assembly_name is required");
		if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
			throw new ArgumentException("type_full_name is required");

		var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
		if (assembly == null)
			throw new ArgumentException($"Assembly not found: {asmNameObj}");

		var type = FindTypeInAssemblyAll(assembly, typeNameObj.ToString() ?? "");
		if (type == null)
			throw new ArgumentException($"Type not found: {typeNameObj}");

		var pinvokes = type.Methods
			.Where(m => m.HasImplMap)
			.Select(m => new {
				ManagedName = m.Name.String,
				Token = $"0x{m.MDToken.Raw:X8}",
				DllName = m.ImplMap?.Module?.Name?.String ?? "",
				NativeName = m.ImplMap?.Name?.String ?? m.Name.String,
				ReturnType = m.ReturnType?.FullName ?? "",
				IsStatic = m.IsStatic,
				CallingConvention = m.ImplMap?.Attributes.ToString() ?? ""
			})
			.ToList();

		var result = JsonSerializer.Serialize(new {
			Type = type.FullName,
			PInvokeCount = pinvokes.Count,
			Methods = pinvokes
		}, new JsonSerializerOptions { WriteIndented = true });

		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = result } }
		};
	}

	/// <summary>
	/// Replaces a method's IL body with a minimal return stub to neutralize it.
	/// Useful for disabling anti-debug, anti-tamper, or other unwanted methods.
	/// Arguments: assembly_name, type_full_name, method_name, method_token (opt)
	/// Changes are in-memory until save_assembly is called.
	/// </summary>
	public CallToolResult PatchMethodToRet(Dictionary<string, object>? arguments) {
		if (arguments == null)
			throw new ArgumentException("Arguments required");
		if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
			throw new ArgumentException("assembly_name is required");
		if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
			throw new ArgumentException("type_full_name is required");
		if (!arguments.TryGetValue("method_name", out var methodNameObj))
			throw new ArgumentException("method_name is required");

		arguments.TryGetValue("method_token", out var methodTokenObj);

		var assemblyName = asmNameObj.ToString() ?? "";
		var typeFullName = typeNameObj.ToString() ?? "";
		var methodName = methodNameObj.ToString() ?? "";

		var assembly = FindAssemblyByName(assemblyName);
		if (assembly == null)
			throw new ArgumentException($"Assembly not found: {assemblyName}");

		// Search type, including nested types
		var type = FindTypeInAssemblyAll(assembly, typeFullName);
		if (type == null)
			throw new ArgumentException($"Type not found: {typeFullName}");

		MethodDef? method = null;

		// Find by token if provided
		if (methodTokenObj != null) {
			var tokenStr = methodTokenObj is System.Text.Json.JsonElement tokenEl
				? (tokenEl.ValueKind == System.Text.Json.JsonValueKind.String ? tokenEl.GetString() ?? "" : tokenEl.GetRawText())
				: (methodTokenObj.ToString() ?? "");
			uint token = ParseToken(tokenStr);
			if (token != 0)
				method = type.Methods.FirstOrDefault(m => m.MDToken.Raw == token);
			if (method == null)
				throw new ArgumentException($"No method with token '{tokenStr}' found in '{typeFullName}'");
		}

		// Find by name if no token provided
		if (method == null) {
			var matches = type.Methods.Where(m => m.Name.String == methodName).ToList();
			if (matches.Count == 0)
				throw new ArgumentException($"Method '{methodName}' not found in type '{typeFullName}'");
			if (matches.Count > 1)
				throw new ArgumentException(
					$"Method '{methodName}' is ambiguous ({matches.Count} overloads in '{typeFullName}'). " +
					$"Use method_token to disambiguate. Tokens: {string.Join(", ", matches.Select(m => $"0x{m.MDToken.Raw:X8}"))}");
			method = matches[0];
		}

		// If this is a P/Invoke method, convert it to a managed IL stub first
		bool wasPInvoke = method.HasImplMap;
		if (wasPInvoke) {
			method.ImplMap = null;
			method.Attributes &= ~MethodAttributes.PinvokeImpl;
			method.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
		}

		// Build minimal return stub
		int oldCount = method.Body?.Instructions.Count ?? 0;
		var newBody = new CilBody();
		var retType = method.ReturnType;

		switch (retType.ElementType) {
			case ElementType.Void:
				break;
			case ElementType.I8:
			case ElementType.U8:
				newBody.Instructions.Add(Instruction.Create(OpCodes.Ldc_I8, 0L));
				break;
			case ElementType.R4:
				newBody.Instructions.Add(Instruction.Create(OpCodes.Ldc_R4, 0f));
				break;
			case ElementType.R8:
				newBody.Instructions.Add(Instruction.Create(OpCodes.Ldc_R8, 0.0));
				break;
			case ElementType.Boolean:
			case ElementType.Char:
			case ElementType.I1:
			case ElementType.U1:
			case ElementType.I2:
			case ElementType.U2:
			case ElementType.I4:
			case ElementType.U4:
			case ElementType.I:
			case ElementType.U:
				newBody.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
				break;
			case ElementType.ValueType: {
				var loc = new Local(retType);
				newBody.Variables.Add(loc);
				newBody.InitLocals = true;
				newBody.Instructions.Add(Instruction.Create(OpCodes.Ldloca_S, loc));
				newBody.Instructions.Add(Instruction.Create(OpCodes.Initobj, retType.ToTypeDefOrRef()));
				newBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc_S, loc));
				break;
			}
			case ElementType.GenericInst: {
				if (retType.IsValueType) {
					var loc = new Local(retType);
					newBody.Variables.Add(loc);
					newBody.InitLocals = true;
					newBody.Instructions.Add(Instruction.Create(OpCodes.Ldloca_S, loc));
					newBody.Instructions.Add(Instruction.Create(OpCodes.Initobj, retType.ToTypeDefOrRef()));
					newBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc_S, loc));
				} else {
					newBody.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
				}
				break;
			}
			default:
				// Reference types (Class, Object, String, SZArray, etc.)
				newBody.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
				break;
		}

		newBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
		method.Body = newBody;

		var result = JsonSerializer.Serialize(new {
			Patched = true,
			WasPInvoke = wasPInvoke,
			TypeFullName = type.FullName,
			MethodName = method.Name.String,
			MethodToken = $"0x{method.MDToken.Raw:X8}",
			ReturnType = retType.FullName,
			OldInstructionCount = oldCount,
			NewInstructionCount = newBody.Instructions.Count,
			Note = "Method body replaced with ret stub. Use save_assembly to persist to disk."
		}, new JsonSerializerOptions { WriteIndented = true });

		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = result } }
		};
	}

	// ── Helpers ─────────────────────────────────────────────────────────────

		static List<object> ExtractCustomAttributes(IList<CustomAttribute> attrs) {
			var result = new List<object>();
			foreach (var ca in attrs) {
				try {
					var ctorArgs = ca.ConstructorArguments
						.Select(a => a.Value?.ToString() ?? "null")
						.ToList();
					var namedArgs = ca.NamedArguments
						.Select(a => (object)new { Name = a.Name, Value = a.Argument.Value?.ToString() ?? "null" })
						.ToList();
					result.Add(new {
						AttributeType = ca.AttributeType?.FullName ?? "Unknown",
						ConstructorArguments = ctorArgs,
						NamedArguments = namedArgs
					});
				}
				catch {
					result.Add(new {
						AttributeType = ca.AttributeType?.FullName ?? "?",
						ConstructorArguments = new List<string>(),
						NamedArguments = new List<object>()
					});
				}
			}
			return result;
		}

		static IEnumerable<TypeDef> GetNestedTypesRecursive(TypeDef type) {
			foreach (var nested in type.NestedTypes) {
				yield return nested;
				foreach (var deep in GetNestedTypesRecursive(nested))
					yield return deep;
			}
		}

		static TypeAttributes ParseTopLevelTypeVisibility(string visibility) => visibility switch {
			"public" => TypeAttributes.Public,
			"private" or "internal" => TypeAttributes.NotPublic,
			_ => throw new ArgumentException($"Invalid visibility for top-level type: '{visibility}'. Use public or internal.")
		};

		static TypeAttributes ParseNestedTypeVisibility(string visibility) => visibility switch {
			"public" => TypeAttributes.NestedPublic,
			"private" => TypeAttributes.NestedPrivate,
			"protected" => TypeAttributes.NestedFamily,
			"internal" => TypeAttributes.NestedAssembly,
			"protected_internal" => TypeAttributes.NestedFamORAssem,
			"private_protected" => TypeAttributes.NestedFamANDAssem,
			_ => throw new ArgumentException($"Invalid visibility: '{visibility}'. Use public/private/protected/internal/protected_internal/private_protected.")
		};

		static MethodAttributes ParseMethodAccess(string visibility) => visibility switch {
			"public" => MethodAttributes.Public,
			"private" => MethodAttributes.Private,
			"protected" => MethodAttributes.Family,
			"internal" => MethodAttributes.Assembly,
			"protected_internal" => MethodAttributes.FamORAssem,
			"private_protected" => MethodAttributes.FamANDAssem,
			_ => throw new ArgumentException($"Invalid visibility: '{visibility}'. Use public/private/protected/internal/protected_internal/private_protected.")
		};

		static FieldAttributes ParseFieldAccess(string visibility) => visibility switch {
			"public" => FieldAttributes.Public,
			"private" => FieldAttributes.Private,
			"protected" => FieldAttributes.Family,
			"internal" => FieldAttributes.Assembly,
			"protected_internal" => FieldAttributes.FamORAssem,
			"private_protected" => FieldAttributes.FamANDAssem,
			_ => throw new ArgumentException($"Invalid visibility: '{visibility}'. Use public/private/protected/internal/protected_internal/private_protected.")
		};

		AssemblyDef? FindAssemblyByName(string name) =>
			documentTreeView.GetAllModuleNodes()
				.Select(m => m.Document?.AssemblyDef)
				.FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));

		TypeDef? FindTypeInAssembly(AssemblyDef assembly, string fullName) =>
			assembly.Modules
				.SelectMany(m => m.Types)
				.FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));

		TypeDef? FindTypeInAssemblyAll(AssemblyDef assembly, string fullName) =>
			assembly.Modules
				.SelectMany(m => GetAllTypesRecursive(m.Types))
				.FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));

		static IEnumerable<TypeDef> GetAllTypesRecursive(IEnumerable<TypeDef> types) {
			foreach (var t in types) {
				yield return t;
				foreach (var n in GetAllTypesRecursive(t.NestedTypes))
					yield return n;
			}
		}

		static uint ParseToken(string s) {
			s = s.Trim();
			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
				if (uint.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var hex))
					return hex;
			}
			if (uint.TryParse(s, out var dec))
				return dec;
			return 0;
		}
	}
}
