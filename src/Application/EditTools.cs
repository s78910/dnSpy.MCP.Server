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
	}
}
