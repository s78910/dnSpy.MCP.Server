using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Application
{
    /// <summary>
    /// Type-focused utilities extracted from McpTools.
    /// Provides: get_type_info, decompile_method, list_methods_in_type, list_properties_in_type,
    /// get_type_fields, get_type_property, get_method_signature, get_constant_values, search_types, find_path_to_type.
    /// This file is standalone and contains the helper methods it requires.
    /// </summary>
    [Export(typeof(TypeTools))]
    public sealed class TypeTools
    {
        readonly IDocumentTreeView documentTreeView;
        readonly IDecompilerService decompilerService;

        [ImportingConstructor]
        public TypeTools(IDocumentTreeView documentTreeView, IDecompilerService decompilerService)
        {
            this.documentTreeView = documentTreeView;
            this.decompilerService = decompilerService;
        }

        public CallToolResult GetTypeInfo(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var allMethods = type.Methods.Select(m => new
            {
                Name = m.Name.String,
                Signature = m.FullName,
                IsPublic = m.IsPublic,
                IsStatic = m.IsStatic,
                IsVirtual = m.IsVirtual,
                IsAbstract = m.IsAbstract,
                ReturnType = m.ReturnType?.FullName ?? "void",
                Parameters = m.Parameters.Select(p => new
                {
                    Name = p.Name,
                    Type = p.Type.FullName
                }).ToList()
            }).ToList();

            var fields = type.Fields.Select(f => new
            {
                Name = f.Name.String,
                Type = f.FieldType.FullName,
                IsPublic = f.IsPublic,
                IsStatic = f.IsStatic,
                IsLiteral = f.IsLiteral
            }).ToList();

            var properties = type.Properties.Select(p => new
            {
                Name = p.Name.String,
                Type = p.PropertySig?.RetType?.FullName ?? "unknown",
                CanRead = p.GetMethod != null,
                CanWrite = p.SetMethod != null
            }).ToList();

            var methodsToReturn = allMethods.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allMethods.Count;
            var isFirstRequest = string.IsNullOrEmpty(cursor);

            var info = new Dictionary<string, object>
            {
                ["FullName"] = type.FullName,
                ["Namespace"] = type.Namespace.String,
                ["Name"] = type.Name.String,
                ["IsPublic"] = type.IsPublic,
                ["IsClass"] = type.IsClass,
                ["IsInterface"] = type.IsInterface,
                ["IsEnum"] = type.IsEnum,
                ["IsValueType"] = type.IsValueType,
                ["IsAbstract"] = type.IsAbstract,
                ["IsSealed"] = type.IsSealed,
                ["BaseType"] = type.BaseType?.FullName ?? "None",
                ["Interfaces"] = type.Interfaces.Select(i => i.Interface.FullName).ToList(),
                ["Methods"] = methodsToReturn,
                ["MethodsTotalCount"] = allMethods.Count,
                ["MethodsReturnedCount"] = methodsToReturn.Count
            };

            if (isFirstRequest)
            {
                info["Fields"] = fields;
                info["FieldsCount"] = fields.Count;
                info["Properties"] = properties;
                info["PropertiesCount"] = properties.Count;
            }
            else
            {
                info["FieldsCount"] = fields.Count;
                info["PropertiesCount"] = properties.Count;
            }

            if (hasMore)
            {
                info["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        public CallToolResult DecompileMethod(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method == null)
                throw new ArgumentException($"Method not found: {methodName}");

            var decompiler = decompilerService.Decompiler;
            var output = new StringBuilderDecompilerOutput();
            var decompilationContext = new DecompilationContext
            {
                CancellationToken = System.Threading.CancellationToken.None
            };

            decompiler.Decompile(method, output, decompilationContext);

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = output.ToString() }
                }
            };
        }

        public CallToolResult ListMethodsInType(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;
            string? visibility = null;
            if (arguments.TryGetValue("visibility", out var visObj))
                visibility = visObj.ToString()?.ToLower();

            string? namePattern = null;
            if (arguments.TryGetValue("name_pattern", out var npObj))
                namePattern = npObj?.ToString();

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            System.Text.RegularExpressions.Regex? nameRegex = null;
            if (!string.IsNullOrEmpty(namePattern))
                nameRegex = BuildPatternRegex(namePattern!);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var methods = type.Methods
                .Where(m =>
                {
                    if (nameRegex != null && !nameRegex.IsMatch(m.Name.String))
                        return false;
                    if (visibility == null) return true;
                    return visibility switch
                    {
                        "public" => m.IsPublic,
                        "private" => m.IsPrivate,
                        "protected" => m.IsFamily,
                        "internal" => m.IsAssembly,
                        _ => false
                    };
                })
                .Select(m => new
                {
                    Name = m.Name.String,
                    ReturnType = m.ReturnType.FullName,
                    IsPublic = m.IsPublic,
                    IsStatic = m.IsStatic,
                    IsVirtual = m.IsVirtual,
                    ParameterCount = m.Parameters.Count
                })
                .ToList();

            return CreatePaginatedResponse(methods, offset, pageSize);
        }

        public CallToolResult ListPropertiesInType(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var properties = type.Properties
                .Select(p => new
                {
                    Name = p.Name.String,
                    PropertyType = p.PropertySig?.RetType?.FullName ?? "Unknown",
                    CanRead = p.GetMethod != null,
                    CanWrite = p.SetMethod != null,
                    IsPublic = (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false),
                    IsStatic = (p.GetMethod?.IsStatic ?? false) || (p.SetMethod?.IsStatic ?? false)
                })
                .ToList();

            return CreatePaginatedResponse(properties, offset, pageSize);
        }

        public CallToolResult GetTypeFields(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("pattern", out var patternObj))
                throw new ArgumentException("pattern is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var pattern = patternObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            var regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var allMatchingFields = type.Fields
                .Where(f => regex.IsMatch(f.Name.String))
                .Select(f => new
                {
                    Name = f.Name.String,
                    Type = f.FieldType.FullName,
                    IsPublic = f.IsPublic,
                    IsStatic = f.IsStatic,
                    IsLiteral = f.IsLiteral,
                    IsReadOnly = f.IsInitOnly,
                    Attributes = f.Attributes.ToString()
                })
                .ToList();

            var fieldsToReturn = allMatchingFields.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allMatchingFields.Count;

            var response = new Dictionary<string, object>
            {
                ["Type"] = typeFullName,
                ["Pattern"] = pattern,
                ["MatchCount"] = allMatchingFields.Count,
                ["ReturnedCount"] = fieldsToReturn.Count,
                ["Fields"] = fieldsToReturn
            };

            if (hasMore)
            {
                response["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        public CallToolResult GetTypeProperty(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("property_name", out var propertyNameObj))
                throw new ArgumentException("property_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var propertyName = propertyNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var property = type.Properties.FirstOrDefault(p => p.Name.String.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (property == null)
                throw new ArgumentException($"Property not found: {propertyName}");

            var propertyInfo = new
            {
                Name = property.Name.String,
                Type = property.PropertySig?.RetType?.FullName ?? "unknown",
                CanRead = property.GetMethod != null,
                CanWrite = property.SetMethod != null,
                GetMethod = property.GetMethod != null ? new
                {
                    Name = property.GetMethod.Name.String,
                    IsPublic = property.GetMethod.IsPublic,
                    IsStatic = property.GetMethod.IsStatic
                } : null,
                SetMethod = property.SetMethod != null ? new
                {
                    Name = property.SetMethod.Name.String,
                    IsPublic = property.SetMethod.IsPublic,
                    IsStatic = property.SetMethod.IsStatic
                } : null,
                Attributes = property.Attributes.ToString(),
                CustomAttributes = property.CustomAttributes.Select(a => a.AttributeType.FullName).ToList()
            };

            var result = JsonSerializer.Serialize(propertyInfo, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        public CallToolResult FindPathToType(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("from_type", out var fromTypeObj))
                throw new ArgumentException("from_type is required");
            if (!arguments.TryGetValue("to_type", out var toTypeObj))
                throw new ArgumentException("to_type is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var fromTypeName = fromTypeObj.ToString() ?? string.Empty;
            var toTypeName = toTypeObj.ToString() ?? string.Empty;

            int maxDepth = 5;
            if (arguments.TryGetValue("max_depth", out var maxDepthObj))
            {
                if (maxDepthObj is JsonElement elem && elem.TryGetInt32(out var depth))
                    maxDepth = depth;
            }

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var fromType = FindTypeInAssembly(assembly, fromTypeName);
            if (fromType == null)
                throw new ArgumentException($"From type not found: {fromTypeName}");

            var toTypeLower = toTypeName.ToLowerInvariant();
            var targetTypes = assembly.Modules
                .SelectMany(m => m.Types)
                .Where(t => t.FullName.ToLowerInvariant().Contains(toTypeLower) ||
                            t.Name.String.ToLowerInvariant().Contains(toTypeLower))
                .ToList();

            if (targetTypes.Count == 0)
                throw new ArgumentException($"Target type not found: {toTypeName}");

            var paths = new List<object>();
            foreach (var targetType in targetTypes)
            {
                var path = FindPathBFS(fromType, targetType, maxDepth);
                if (path != null)
                    paths.Add(path);
            }

            if (paths.Count == 0)
            {
                return new CallToolResult
                {
                    Content = new List<ToolContent> {
                        new ToolContent { Text = $"No path found from {fromTypeName} to {toTypeName} within depth {maxDepth}" }
                    }
                };
            }

            var result = JsonSerializer.Serialize(new
            {
                FromType = fromTypeName,
                ToType = toTypeName,
                PathsFound = paths.Count,
                Paths = paths
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        object? FindPathBFS(TypeDef fromType, TypeDef toType, int maxDepth)
        {
            var queue = new Queue<(TypeDef type, List<string> path)>();
            var visited = new HashSet<string>();

            queue.Enqueue((fromType, new List<string> { fromType.Name.String }));
            visited.Add(fromType.FullName);

            while (queue.Count > 0)
            {
                var (currentType, currentPath) = queue.Dequeue();

                if (currentPath.Count > maxDepth + 1)
                    continue;

                if (currentType.FullName == toType.FullName)
                {
                    return new
                    {
                        Path = string.Join(" -> ", currentPath),
                        Depth = currentPath.Count - 1,
                        Steps = currentPath
                    };
                }

                foreach (var prop in currentType.Properties)
                {
                    var propType = prop.PropertySig?.RetType?.ToTypeDefOrRef()?.ResolveTypeDef();
                    if (propType != null && !visited.Contains(propType.FullName))
                    {
                        visited.Add(propType.FullName);
                        var newPath = new List<string>(currentPath) { prop.Name.String };
                        queue.Enqueue((propType, newPath));
                    }
                }

                foreach (var field in currentType.Fields)
                {
                    var fieldType = field.FieldType?.ToTypeDefOrRef()?.ResolveTypeDef();
                    if (fieldType != null && !visited.Contains(fieldType.FullName))
                    {
                        visited.Add(fieldType.FullName);
                        var newPath = new List<string>(currentPath) { field.Name.String };
                        queue.Enqueue((fieldType, newPath));
                    }
                }
            }

            return null;
        }

        AssemblyDef? FindAssemblyByName(string name)
        {
            return documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        TypeDef? FindTypeInAssembly(AssemblyDef assembly, string fullName)
        {
            return assembly.Modules
                .SelectMany(m => m.Types)
                .FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));
        }

        string EncodeCursor(int offset, int pageSize)
        {
            var cursorData = new { offset, pageSize };
            var json = JsonSerializer.Serialize(cursorData);
            var bytes = Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes);
        }

        static System.Text.RegularExpressions.Regex BuildPatternRegex(string pattern)
        {
            bool isRegex = pattern.IndexOfAny(new[] { '^', '$', '[', '(', '|', '+', '{' }) >= 0;
            if (isRegex)
                return new System.Text.RegularExpressions.Regex(pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var escaped = System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*", ".*").Replace(@"\?", ".");
            return new System.Text.RegularExpressions.Regex("^" + escaped + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        (int offset, int pageSize) DecodeCursor(string? cursor)
        {
            const int defaultPageSize = 50;
            if (string.IsNullOrEmpty(cursor))
                return (0, defaultPageSize);

            try
            {
                var bytes = Convert.FromBase64String(cursor);
                var json = Encoding.UTF8.GetString(bytes);
                var cursorData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (cursorData == null)
                    throw new ArgumentException("Invalid cursor: cursor data is null");

                if (!cursorData.TryGetValue("offset", out var offsetObj) || !(offsetObj is JsonElement offsetElem) || !offsetElem.TryGetInt32(out var offset))
                    throw new ArgumentException("Invalid cursor: missing or invalid 'offset' field");

                if (!cursorData.TryGetValue("pageSize", out var pageSizeObj) || !(pageSizeObj is JsonElement pageSizeElem) || !pageSizeElem.TryGetInt32(out var pageSize))
                    throw new ArgumentException("Invalid cursor: missing or invalid 'pageSize' field");

                if (offset < 0)
                    throw new ArgumentException("Invalid cursor: offset cannot be negative");

                if (pageSize <= 0)
                    throw new ArgumentException("Invalid cursor: pageSize must be positive");

                return (offset, pageSize);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid cursor: {ex.Message}");
            }
        }

        CallToolResult CreatePaginatedResponse<T>(List<T> allItems, int offset, int pageSize)
        {
            var itemsToReturn = allItems.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allItems.Count;

            var response = new Dictionary<string, object>
            {
                ["items"] = itemsToReturn,
                ["total_count"] = allItems.Count,
                ["returned_count"] = itemsToReturn.Count
            };

            if (hasMore)
            {
                response["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        public CallToolResult GetMethodIL(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var method = type.Methods.FirstOrDefault(m => m.Name.String == methodName);
            if (method == null)
                throw new ArgumentException($"Method not found: {methodName}");

            if (method.Body == null)
                throw new ArgumentException($"Method has no body (abstract or native)");

            var instructions = method.Body.Instructions;
            var ilInstructions = new List<object>();

            foreach (var instr in instructions)
            {
                var ilInstr = new Dictionary<string, object>
                {
                    ["offset"] = instr.Offset,
                    ["opcode"] = instr.OpCode.Name,
                    ["operand"] = GetOperandString(instr)
                };
                ilInstructions.Add(ilInstr);
            }

            var locals = new List<object>();
            if (method.Body.Variables != null)
            {
                foreach (var local in method.Body.Variables)
                {
                    locals.Add(new
                    {
                        Index = local.Index,
                        Type = local.Type.FullName,
                        Name = local.Name ?? $"V_{local.Index}"
                    });
                }
            }

            var result = JsonSerializer.Serialize(new
            {
                Method = method.FullName,
                MaxStack = method.Body.MaxStack,
                LocalVarCount = method.Body.Variables?.Count ?? 0,
                Locals = locals,
                Instructions = ilInstructions,
                InstructionCount = instructions.Count
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        public CallToolResult GetMethodILBytes(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var method = type.Methods.FirstOrDefault(m => m.Name.String == methodName);
            if (method == null)
                throw new ArgumentException($"Method not found: {methodName}");

            if (method.Body == null)
                throw new ArgumentException($"Method has no body (abstract or native)");

            var cilBody = method.Body;
            var instructions = cilBody.Instructions;
            var bytes = new List<byte>();
            foreach (var instr in instructions)
            {
                var opcode = instr.OpCode;
                var code = opcode.Code;
                if (code >= dnlib.DotNet.Emit.Code.Nop)
                {
                    if ((int)code > 255)
                    {
                        bytes.Add(0xFE);
                        bytes.Add((byte)((int)code - 256));
                    }
                    else
                    {
                        bytes.Add((byte)code);
                    }
                }
            }
            var byteArray = bytes.ToArray();
            var hexString = BitConverter.ToString(byteArray).Replace("-", " ");

            var result = JsonSerializer.Serialize(new
            {
                Method = method.FullName,
                ByteCount = byteArray?.Length ?? 0,
                ILBytes = hexString,
                ILBytesBase64 = byteArray != null ? Convert.ToBase64String(byteArray) : ""
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        public CallToolResult GetMethodExceptionHandlers(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var method = type.Methods.FirstOrDefault(m => m.Name.String == methodName);
            if (method == null)
                throw new ArgumentException($"Method not found: {methodName}");

            if (method.Body == null)
                throw new ArgumentException($"Method has no body (abstract or native)");

            var handlers = new List<object>();
            foreach (var eh in method.Body.ExceptionHandlers)
            {
                handlers.Add(new
                {
                    HandlerType = eh.HandlerType.ToString(),
                    TryStart = (int)(eh.TryStart?.Offset ?? 0),
                    TryEnd = (int)(eh.TryEnd?.Offset ?? 0),
                    HandlerStart = (int)(eh.HandlerStart?.Offset ?? 0),
                    HandlerEnd = (int)(eh.HandlerEnd?.Offset ?? 0),
                    CatchType = eh.CatchType?.FullName ?? "null",
                    FilterStart = eh.FilterStart != null ? (int)eh.FilterStart.Offset : -1
                });
            }

            var result = JsonSerializer.Serialize(new
            {
                Method = method.FullName,
                ExceptionHandlerCount = handlers.Count,
                ExceptionHandlers = handlers
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        string GetOperandString(dnlib.DotNet.Emit.Instruction instr)
        {
            if (instr.Operand == null)
                return "";
            
            if (instr.Operand is MethodDef md)
                return md.FullName;
            if (instr.Operand is FieldDef fd)
                return fd.FullName;
            if (instr.Operand is TypeDef td)
                return td.FullName;
            if (instr.Operand is MemberRef mr)
                return mr.FullName;
            if (instr.Operand is string s)
                return $"\"{s}\"";
            if (instr.Operand is ITypeDefOrRef tdor)
                return tdor.FullName;
            if (instr.Operand is MethodSpec ms)
                return ms.FullName;
            if (instr.Operand is ParamDef pd)
                return pd.Name;
            if (instr.Operand is dnlib.DotNet.Emit.Instruction target)
                return $"IL_{target.Offset:X4}";
            if (instr.Operand is dnlib.DotNet.Emit.Instruction[] targets)
                return string.Join(", ", targets.Select(t => $"IL_{t.Offset:X4}"));
            
            return instr.Operand.ToString() ?? "";
        }
    }
}