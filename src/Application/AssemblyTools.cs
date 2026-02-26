using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Application
{
    /// <summary>
    /// Assembly-focused utilities extracted from McpTools.
    /// Provides: list_assemblies, get_assembly_info, list_types and list_native_modules.
    /// </summary>
    [Export(typeof(AssemblyTools))]
    public sealed class AssemblyTools
    {
        readonly IDocumentTreeView documentTreeView;

        [ImportingConstructor]
        public AssemblyTools(IDocumentTreeView documentTreeView)
        {
            this.documentTreeView = documentTreeView;
        }

        public CallToolResult ListAssemblies()
        {
            var assemblies = documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .Where(a => a != null)
                .Distinct()
                .Select(a => new
                {
                    Name = a!.Name.String,
                    Version = a.Version?.ToString() ?? "N/A",
                    FullName = a.FullName,
                    Culture = a.Culture ?? "neutral",
                    PublicKeyToken = a.PublicKeyToken?.ToString() ?? "null"
                })
                .ToList();

            var result = JsonSerializer.Serialize(assemblies, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        public CallToolResult GetAssemblyInfo(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var modules = assembly.Modules.Select(m => new
            {
                Name = m.Name.String,
                Kind = m.Kind.ToString(),
                Architecture = m.Machine.ToString(),
                RuntimeVersion = m.RuntimeVersion
            }).ToList();

            var allNamespaces = assembly.Modules
                .SelectMany(m => m.Types)
                .Select(t => t.Namespace.String)
                .Distinct()
                .OrderBy(ns => ns)
                .ToList();

            var namespacesToReturn = allNamespaces.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allNamespaces.Count;

            var info = new Dictionary<string, object>
            {
                ["Name"] = assembly.Name.String,
                ["Version"] = assembly.Version?.ToString() ?? "N/A",
                ["FullName"] = assembly.FullName,
                ["Culture"] = assembly.Culture ?? "neutral",
                ["PublicKeyToken"] = assembly.PublicKeyToken?.ToString() ?? "null",
                ["Modules"] = modules,
                ["Namespaces"] = namespacesToReturn,
                ["NamespacesTotalCount"] = allNamespaces.Count,
                ["NamespacesReturnedCount"] = namespacesToReturn.Count,
                ["TypeCount"] = assembly.Modules.Sum(m => m.Types.Count)
            };

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

        public CallToolResult ListTypes(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            string? namespaceFilter = null;
            if (arguments.TryGetValue("namespace", out var nsObj))
                namespaceFilter = nsObj.ToString();

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

            var types = assembly.Modules
                .SelectMany(m => m.Types)
                .Where(t => {
                    if (!string.IsNullOrEmpty(namespaceFilter) &&
                        !t.Namespace.String.Equals(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (nameRegex != null)
                        return nameRegex.IsMatch(t.Name.String) || nameRegex.IsMatch(t.FullName);
                    return true;
                })
                .Select(t => new
                {
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    IsPublic = t.IsPublic,
                    IsClass = t.IsClass,
                    IsInterface = t.IsInterface,
                    IsEnum = t.IsEnum,
                    IsValueType = t.IsValueType,
                    IsAbstract = t.IsAbstract,
                    IsSealed = t.IsSealed,
                    BaseType = t.BaseType?.FullName ?? "None"
                })
                .ToList();

            return CreatePaginatedResponse(types, offset, pageSize);
        }

        public CallToolResult ListNativeModules(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var modules = new Dictionary<string, HashSet<object>>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in assembly.Modules.SelectMany(m => m.Types))
            {
                foreach (var method in type.Methods)
                {
                    try
                    {
                        foreach (var ca in method.CustomAttributes)
                        {
                            var at = ca.AttributeType.FullName;
                            if (!string.IsNullOrEmpty(at) && at.EndsWith("DllImportAttribute"))
                            {
                                var dllName = ca.ConstructorArguments.Count > 0 ? ca.ConstructorArguments[0].Value?.ToString() ?? string.Empty : string.Empty;
                                if (string.IsNullOrEmpty(dllName))
                                    continue;

                                if (!modules.TryGetValue(dllName, out var set))
                                {
                                    set = new HashSet<object>();
                                    modules[dllName] = set;
                                }

                                set.Add(new { Type = type.FullName, Method = method.Name.String });
                            }
                        }
                    }
                    catch { /* tolerate metadata issues */ }
                }
            }

            var list = modules.Select(kvp => new
            {
                ModuleName = kvp.Key,
                PathHint = string.Empty,
                ImportedBy = kvp.Value.ToList()
            }).ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
        }

        /// <summary>
        /// Scans the raw PE file bytes for printable ASCII and UTF-16 strings.
        /// Useful for finding URLs, keys, and other plaintext data in obfuscated assemblies.
        /// </summary>
        public CallToolResult ScanPeStrings(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var asmObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = asmObj.ToString() ?? string.Empty;

            // Get file path from the document node
            var moduleNode = documentTreeView.GetAllModuleNodes()
                .FirstOrDefault(m => m.Document?.AssemblyDef != null &&
                    m.Document.AssemblyDef.Name.String.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));

            if (moduleNode == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var filePath = moduleNode.Document?.Filename;
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                throw new ArgumentException($"File not found on disk: {filePath ?? "(null)"}");

            int minLength = 5;
            if (arguments.TryGetValue("min_length", out var minLenObj) &&
                int.TryParse(minLenObj.ToString(), out var ml) && ml > 0)
                minLength = ml;

            bool includeUtf16 = true;
            if (arguments.TryGetValue("include_utf16", out var utf16Obj))
                bool.TryParse(utf16Obj.ToString(), out includeUtf16);

            string? filterPattern = null;
            if (arguments.TryGetValue("filter_pattern", out var fObj))
                filterPattern = fObj.ToString();

            System.Text.RegularExpressions.Regex? filterRx = null;
            if (!string.IsNullOrEmpty(filterPattern))
                filterRx = new System.Text.RegularExpressions.Regex(filterPattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            var bytes = System.IO.File.ReadAllBytes(filePath);
            var found = new List<(string Encoding, string Offset, string Value)>();
            var seen = new HashSet<string>();

            // Scan ASCII strings
            int start = -1;
            for (int i = 0; i <= bytes.Length; i++)
            {
                bool printable = i < bytes.Length && bytes[i] >= 0x20 && bytes[i] < 0x7F;
                if (printable)
                {
                    if (start < 0) start = i;
                }
                else
                {
                    if (start >= 0)
                    {
                        int len = i - start;
                        if (len >= minLength)
                        {
                            var s = Encoding.ASCII.GetString(bytes, start, len);
                            if ((filterRx == null || filterRx.IsMatch(s)) && seen.Add(s))
                                found.Add(("ASCII", $"0x{start:X}", s));
                        }
                        start = -1;
                    }
                }
            }

            // Scan UTF-16 LE strings
            if (includeUtf16)
            {
                start = -1;
                for (int i = 0; i <= bytes.Length - 1; i += 2)
                {
                    bool printable = i + 1 < bytes.Length && bytes[i] >= 0x20 && bytes[i] < 0x7F && bytes[i + 1] == 0x00;
                    if (printable)
                    {
                        if (start < 0) start = i;
                    }
                    else
                    {
                        if (start >= 0)
                        {
                            int len = i - start;
                            if (len / 2 >= minLength)
                            {
                                var s = Encoding.Unicode.GetString(bytes, start, len);
                                if ((filterRx == null || filterRx.IsMatch(s)) && seen.Add(s))
                                    found.Add(("UTF-16", $"0x{start:X}", s));
                            }
                            start = -1;
                        }
                    }
                }
            }

            // Highlight suspicious strings (URLs, IPs, emails, paths)
            var suspicious = new System.Text.RegularExpressions.Regex(
                @"https?://|ftp://|\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}|@[a-z0-9-]+\.[a-z]{2,}|[A-Z]:\\|/[a-z]+/|[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var allStrings = found.Select(t => new { Encoding = t.Encoding, Offset = t.Offset, Value = t.Value }).ToList();
            var suspiciousStrings = found
                .Where(t => suspicious.IsMatch(t.Value))
                .Select(t => new { Encoding = t.Encoding, Offset = t.Offset, Value = t.Value })
                .ToList();

            var result = JsonSerializer.Serialize(new
            {
                FilePath = filePath,
                FileSize = bytes.Length,
                TotalStrings = found.Count,
                SuspiciousStrings = suspiciousStrings,
                AllStrings = allStrings
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        AssemblyDef? FindAssemblyByName(string name)
        {
            return documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
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
    }
}