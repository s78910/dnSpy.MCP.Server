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
using System.Reflection;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Application;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application
{
    [Export(typeof(McpTools))]
    public sealed class McpTools
    {
        readonly IDocumentTreeView documentTreeView;
        readonly IDecompilerService decompilerService;
        readonly Lazy<AssemblyTools> assemblyTools;
        readonly Lazy<TypeTools> typeTools;
        readonly Lazy<EditTools> editTools;
        readonly Lazy<DebugTools> debugTools;
        readonly Lazy<DumpTools> dumpTools;
        readonly Lazy<MemoryInspectTools> memoryInspectTools;

        [ImportingConstructor]
        public McpTools(
            IDocumentTreeView documentTreeView,
            IDecompilerService decompilerService,
            Lazy<AssemblyTools> assemblyTools,
            Lazy<TypeTools> typeTools,
            Lazy<EditTools> editTools,
            Lazy<DebugTools> debugTools,
            Lazy<DumpTools> dumpTools,
            Lazy<MemoryInspectTools> memoryInspectTools)
        {
            this.documentTreeView = documentTreeView;
            this.decompilerService = decompilerService;
            this.assemblyTools = assemblyTools;
            this.typeTools = typeTools;
            this.editTools = editTools;
            this.debugTools = debugTools;
            this.dumpTools = dumpTools;
            this.memoryInspectTools = memoryInspectTools;
        }

        public List<ToolInfo> GetAvailableTools()
        {
            return new List<ToolInfo> {
                new ToolInfo {
                    Name = "list_assemblies",
                    Description = "List all loaded assemblies in dnSpy",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "get_assembly_info",
                    Description = "Get detailed information about a specific assembly",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "list_types",
                    Description = "List types in an assembly or namespace. Supports glob (System.* or *Controller) and regex (^System\\..*Controller$) via name_pattern.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["namespace"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional exact namespace filter"
                            },
                            ["name_pattern"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional name filter: glob (* and ?) or regex (use ^/$). Matches against type short name and full name."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Pagination cursor from previous response nextCursor"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "get_type_info",
                    Description = "Get detailed information about a specific type",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "decompile_method",
                    Description = "Decompile a specific method to C# code",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the method"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "search_types",
                    Description = "Search for types by name across all loaded assemblies. Supports glob wildcards (*IService*) and regex (^My\\..*Repository$).",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["query"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Search query: plain substring, glob (* and ?), or regex (use ^/$). Matched against FullName."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Pagination cursor from previous response nextCursor"
                            }
                        },
                        ["required"] = new List<string> { "query" }
                    }
                },
                new ToolInfo {
                    Name = "list_methods_in_type",
                    Description = "List methods in a type. Filter by visibility and/or name pattern (glob or regex).",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["visibility"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional visibility filter: public, private, protected, or internal"
                            },
                            ["name_pattern"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional name filter: glob (* and ?) or regex (use ^/$). E.g. 'Get*', '^On[A-Z]', 'Async$'"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Pagination cursor from previous response nextCursor"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "list_properties_in_type",
                    Description = "List all properties in a type",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "get_method_signature",
                    Description = "Get detailed method signature",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the method"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_who_calls_method",
                    Description = "Find all methods that call a specific method",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the method"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "analyze_type_inheritance",
                    Description = "Analyze complete inheritance chain of a type",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "list_tools",
                    Description = "List all available MCP tools",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>()
                    }
                },
                new ToolInfo {
                    Name = "get_method_il",
                    Description = "Get IL instructions of a method",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the method"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "get_method_il_bytes",
                    Description = "Get raw IL bytes of a method",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the method"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "get_method_exception_handlers",
                    Description = "Get exception handlers (try-catch-finally) of a method",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type"
                            },
                            ["method_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the method"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },

                // ── Edit Tools ──────────────────────────────────────────────────────
                new ToolInfo {
                    Name = "decompile_type",
                    Description = "Decompile an entire type (class/struct/interface/enum) to C# source code",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type (e.g. MyNamespace.MyClass)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "change_member_visibility",
                    Description = "Change the visibility/access modifier of a type or its members (method, field, property, event). Changes are in-memory until save_assembly is called.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the containing type (or the type itself when member_kind=type)" },
                            ["member_kind"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Kind of member: type, method, field, property, or event" },
                            ["member_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the member (ignored when member_kind=type)" },
                            ["new_visibility"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New visibility: public, private, protected, internal, protected_internal, private_protected" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "member_kind", "new_visibility" }
                    }
                },
                new ToolInfo {
                    Name = "rename_member",
                    Description = "Rename a type or one of its members (method, field, property, event). Changes are in-memory until save_assembly is called.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["member_kind"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Kind of member: type, method, field, property, or event" },
                            ["old_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Current name of the member" },
                            ["new_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New name for the member" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "member_kind", "old_name", "new_name" }
                    }
                },
                new ToolInfo {
                    Name = "save_assembly",
                    Description = "Save a (possibly modified) assembly to disk. Persists all in-memory changes made by rename_member, change_member_visibility, edit_assembly_metadata, etc.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to save" },
                            ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Output file path. Defaults to the original file location." }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },

                // ── Assembly metadata & flags ──────────────────────────────────────────
                new ToolInfo {
                    Name = "get_assembly_metadata",
                    Description = "Read assembly-level metadata: name, version, culture, public key, flags, hash algorithm, module count, and custom attributes.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "edit_assembly_metadata",
                    Description = "Edit assembly-level metadata fields: name, version, culture, or hash algorithm. Changes are in-memory until save_assembly is called.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"]   = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to edit" },
                            ["new_name"]        = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New assembly name (optional)" },
                            ["version"]         = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New version as major.minor.build.revision (optional, e.g. '2.0.0.0')" },
                            ["culture"]         = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New culture string, e.g. '' (neutral), 'en-US' (optional)" },
                            ["hash_algorithm"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Hash algorithm: SHA1, MD5, or None (optional)" }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "set_assembly_flags",
                    Description = "Set or clear an individual assembly attribute flag. Changes are in-memory until save_assembly is called.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["flag_name"]     = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Flag to change: PublicKey | Retargetable | DisableJITOptimizer | EnableJITTracking | WindowsRuntime | ProcessorArchitecture"
                            },
                            ["value"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "true/false for boolean flags; architecture name for ProcessorArchitecture (AnyCPU | x86 | AMD64 | ARM | ARM64 | IA64)"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "flag_name", "value" }
                    }
                },

                // ── Assembly references / DLL injection ────────────────────────────────
                new ToolInfo {
                    Name = "list_assembly_references",
                    Description = "List all assembly references (AssemblyRef table entries) in the manifest module.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "add_assembly_reference",
                    Description = "Add an assembly reference (AssemblyRef) by loading a DLL from disk. A TypeForwarder is created to anchor the reference so it persists when saved. Changes are in-memory until save_assembly is called.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the target assembly to add the reference to" },
                            ["dll_path"]      = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to the DLL to reference" },
                            ["type_name"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Specific public type to use as the TypeForwarder anchor (optional; defaults to first public type)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "dll_path" }
                    }
                },
                new ToolInfo {
                    Name = "inject_type_from_dll",
                    Description = "Deep-clone a type (fields, methods with IL, properties, events) from an external DLL file into the target assembly. Changes are in-memory until save_assembly is called.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"]    = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the target assembly" },
                            ["dll_path"]         = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to the source DLL" },
                            ["source_type"]      = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name (or simple name) of the type to inject" },
                            ["target_namespace"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Namespace for the injected type in the target assembly (optional; defaults to source namespace)" },
                            ["overwrite"]        = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Replace existing type with same name/namespace if present (default false)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "dll_path", "source_type" }
                    }
                },

                new ToolInfo {
                    Name = "list_events_in_type",
                    Description = "List all events defined in a type",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "get_custom_attributes",
                    Description = "Get custom attributes on a type or one of its members. Omit member_name to get the type's own attributes.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["member_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the member (optional; omit for type-level attributes)" },
                            ["member_kind"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Kind of member: method, field, property, or event (optional; helps disambiguation)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "list_nested_types",
                    Description = "List all nested types inside a type, recursively",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the containing type" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },

                // ── Debug Tools ─────────────────────────────────────────────────────
                new ToolInfo {
                    Name = "get_debugger_state",
                    Description = "Get the current debugger state: whether debugging is active, running or paused, and process information",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "list_breakpoints",
                    Description = "List all code breakpoints currently registered in dnSpy",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "set_breakpoint",
                    Description = "Set a breakpoint at a method entry point (or specific IL offset). The breakpoint persists across debug sessions.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method" },
                            ["il_offset"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "IL offset within the method body (default 0 = method entry)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "remove_breakpoint",
                    Description = "Remove a breakpoint from a specific method and IL offset",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method" },
                            ["il_offset"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "IL offset of the breakpoint (default 0)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "clear_all_breakpoints",
                    Description = "Remove all visible breakpoints",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "continue_debugger",
                    Description = "Resume execution of all paused debugged processes",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "break_debugger",
                    Description = "Pause all currently running debugged processes",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "stop_debugging",
                    Description = "Stop all active debug sessions",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "get_call_stack",
                    Description = "Get the call stack of the current thread when the debugger is paused. Use break_debugger to pause first.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },

                // ── Previously-hidden TypeTools ──────────────────────────────────
                new ToolInfo {
                    Name = "get_type_fields",
                    Description = "List fields in a type matching a glob/regex pattern. Supports * and ? wildcards.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["pattern"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Field name pattern: glob (* ?) or regex (^/$). Use * to list all." },
                            ["cursor"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Pagination cursor" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "pattern" }
                    }
                },
                new ToolInfo {
                    Name = "get_type_property",
                    Description = "Get detailed information about a single property, including getter/setter info and custom attributes.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["property_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the property" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "property_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_path_to_type",
                    Description = "Find property/field reference paths from one type to another via BFS traversal of the object graph.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["from_type"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the starting type" },
                            ["to_type"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name (or substring) of the target type" },
                            ["max_depth"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum BFS depth (default 5)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "from_type", "to_type" }
                    }
                },
                new ToolInfo {
                    Name = "list_native_modules",
                    Description = "List all native DLLs imported via P/Invoke (DllImport) in an assembly, grouped by DLL name.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },

                // ── Memory Dump Tools ────────────────────────────────────────────
                new ToolInfo {
                    Name = "list_runtime_modules",
                    Description = "List all .NET modules loaded in the currently debugged processes. Requires an active debug session.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: filter by process ID" },
                            ["name_filter"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional: filter by module name (glob or regex)" }
                        },
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "dump_module_from_memory",
                    Description = "Dump a loaded .NET module from process memory to a file. Uses IDbgDotNetRuntime for .NET modules (best quality), falling back to raw ReadMemory. Requires paused or active debug session.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["module_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Module name, filename, or basename (e.g. MyApp.dll)" },
                            ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to write the dumped module (e.g. C:\\dump\\MyApp_dumped.dll)" },
                            ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID when multiple processes are debugged" }
                        },
                        ["required"] = new List<string> { "module_name", "output_path" }
                    }
                },
                new ToolInfo {
                    Name = "read_process_memory",
                    Description = "Read raw bytes from a debugged process address and return a formatted hex dump. Requires active debug session.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["address"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Memory address as hex string (0x7FF000) or decimal" },
                            ["size"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of bytes to read (1-65536)" },
                            ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                        },
                        ["required"] = new List<string> { "address", "size" }
                    }
                },

                // ── Memory Inspect / Runtime Variable Tools ─────────────────────────
                new ToolInfo {
                    Name = "get_local_variables",
                    Description = "Read local variables and parameters from a paused debug session stack frame. Returns primitive values, strings, and addresses for complex objects. Requires the debugger to be paused at a breakpoint.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["frame_index"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Stack frame index (0 = innermost/current frame, default 0)" },
                            ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID when multiple processes are being debugged" }
                        },
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "get_pe_sections",
                    Description = "List PE sections (headers) of a module loaded in the debugged process memory. Returns section names, virtual addresses, sizes, and characteristics. Requires active debug session.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["module_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Module name, filename, or basename (e.g. MyApp.dll)" },
                            ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                        },
                        ["required"] = new List<string> { "module_name" }
                    }
                },
                new ToolInfo {
                    Name = "dump_pe_section",
                    Description = "Extract a specific PE section (e.g. .text, .data, .rsrc) from a module in process memory. Writes to file and/or returns base64-encoded bytes. Requires active debug session.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["module_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Module name, filename, or basename (e.g. MyApp.dll)" },
                            ["section_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "PE section name, e.g. .text, .data, .rsrc, .rdata" },
                            ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional: absolute path to write the section bytes to disk" },
                            ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                        },
                        ["required"] = new List<string> { "module_name", "section_name" }
                    }
                },
                new ToolInfo {
                    Name = "dump_module_unpacked",
                    Description = "Dump a full module from process memory with memory-to-file layout conversion. Produces a valid PE file suitable for loading in dnSpy/IDA. Handles .NET, native, and mixed-mode modules. Requires active debug session.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["module_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Module name, filename, or basename (e.g. MyApp.dll)" },
                            ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to write the dumped PE file" },
                            ["try_fix_pe_layout"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Convert memory layout to file layout (section VA→PointerToRawData remapping). Default true." },
                            ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                        },
                        ["required"] = new List<string> { "module_name", "output_path" }
                    }
                },
                new ToolInfo {
                    Name = "dump_memory_to_file",
                    Description = "Save a contiguous range of process memory directly to a file. Supports large ranges up to 256 MB. Useful for dumping unpacked payloads or large data buffers. Requires active debug session.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["address"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Start address as hex (0x7FF000) or decimal" },
                            ["size"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of bytes to dump (1 to 268435456 / 256 MB)" },
                            ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to write the memory dump" },
                            ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                        },
                        ["required"] = new List<string> { "address", "size", "output_path" }
                    }
                }
            };
        }

        public CallToolResult ExecuteTool(string toolName, Dictionary<string, object>? arguments)
        {
            McpLogger.Info($"Executing tool: {toolName}");

            try
            {
                var result = toolName switch
                {
                    "list_tools" => ListTools(),
                    "list_assemblies" => InvokeLazy(assemblyTools, "ListAssemblies", null),
                    "get_assembly_info" => InvokeLazy(assemblyTools, "GetAssemblyInfo", arguments),
                    "list_types" => InvokeLazy(assemblyTools, "ListTypes", arguments),
                    "get_type_info" => InvokeLazy(typeTools, "GetTypeInfo", arguments),
                    "decompile_method" => InvokeLazy(typeTools, "DecompileMethod", arguments),
                    "list_methods_in_type" => InvokeLazy(typeTools, "ListMethodsInType", arguments),
                    "list_properties_in_type" => InvokeLazy(typeTools, "ListPropertiesInType", arguments),
                    "get_method_signature" => InvokeLazy(typeTools, "GetMethodSignature", arguments),
                    "search_types" => SearchTypes(arguments),
                    "find_who_calls_method" => FindWhoCallsMethod(arguments),
                    "analyze_type_inheritance" => AnalyzeTypeInheritance(arguments),
                    "get_method_il" => InvokeLazy(typeTools, "GetMethodIL", arguments),
                    "get_method_il_bytes" => InvokeLazy(typeTools, "GetMethodILBytes", arguments),
                    "get_method_exception_handlers" => InvokeLazy(typeTools, "GetMethodExceptionHandlers", arguments),

                    // Edit tools
                    "decompile_type" => InvokeLazy(editTools, "DecompileType", arguments),
                    "change_member_visibility" => InvokeLazy(editTools, "ChangeVisibility", arguments),
                    "rename_member" => InvokeLazy(editTools, "RenameMember", arguments),
                    "save_assembly" => InvokeLazy(editTools, "SaveAssembly", arguments),
                    "get_assembly_metadata" => InvokeLazy(editTools, "GetAssemblyMetadata", arguments),
                    "edit_assembly_metadata" => InvokeLazy(editTools, "EditAssemblyMetadata", arguments),
                    "set_assembly_flags" => InvokeLazy(editTools, "SetAssemblyFlags", arguments),
                    "list_assembly_references" => InvokeLazy(editTools, "ListAssemblyReferences", arguments),
                    "add_assembly_reference" => InvokeLazy(editTools, "AddAssemblyReference", arguments),
                    "inject_type_from_dll" => InvokeLazy(editTools, "InjectTypeFromDll", arguments),
                    "list_events_in_type" => InvokeLazy(editTools, "ListEventsInType", arguments),
                    "get_custom_attributes" => InvokeLazy(editTools, "GetCustomAttributes", arguments),
                    "list_nested_types" => InvokeLazy(editTools, "ListNestedTypes", arguments),

                    // Previously-hidden TypeTools
                    "get_type_fields" => InvokeLazy(typeTools, "GetTypeFields", arguments),
                    "get_type_property" => InvokeLazy(typeTools, "GetTypeProperty", arguments),
                    "find_path_to_type" => InvokeLazy(typeTools, "FindPathToType", arguments),
                    "list_native_modules" => InvokeLazy(assemblyTools, "ListNativeModules", arguments),

                    // Memory dump tools
                    "list_runtime_modules" => InvokeLazy(dumpTools, "ListRuntimeModules", arguments),
                    "dump_module_from_memory" => InvokeLazy(dumpTools, "DumpModuleFromMemory", arguments),
                    "read_process_memory" => InvokeLazy(dumpTools, "ReadProcessMemory", arguments),
                    "get_pe_sections" => InvokeLazy(dumpTools, "GetPeSections", arguments),
                    "dump_pe_section" => InvokeLazy(dumpTools, "DumpPeSection", arguments),
                    "dump_module_unpacked" => InvokeLazy(dumpTools, "DumpModuleUnpacked", arguments),
                    "dump_memory_to_file" => InvokeLazy(dumpTools, "DumpMemoryToFile", arguments),

                    // Memory inspect / runtime variable tools
                    "get_local_variables" => InvokeLazy(memoryInspectTools, "GetLocalVariables", arguments),

                    // Debug tools
                    "get_debugger_state" => InvokeLazy(debugTools, "GetDebuggerState", arguments),
                    "list_breakpoints" => InvokeLazy(debugTools, "ListBreakpoints", arguments),
                    "set_breakpoint" => InvokeLazy(debugTools, "SetBreakpoint", arguments),
                    "remove_breakpoint" => InvokeLazy(debugTools, "RemoveBreakpoint", arguments),
                    "clear_all_breakpoints" => InvokeLazy(debugTools, "ClearAllBreakpoints", arguments),
                    "continue_debugger" => InvokeLazy(debugTools, "ContinueDebugger", arguments),
                    "break_debugger" => InvokeLazy(debugTools, "BreakDebugger", arguments),
                    "stop_debugging" => InvokeLazy(debugTools, "StopDebugging", arguments),
                    "get_call_stack" => InvokeLazy(debugTools, "GetCallStack", arguments),

                    _ => new CallToolResult
                    {
                        Content = new List<ToolContent> {
                            new ToolContent { Text = $"Unknown tool: {toolName}" }
                        },
                        IsError = true
                    }
                };

                return result;
            }
            catch (Exception ex)
            {
                McpLogger.Exception(ex, $"Error executing tool {toolName}");
                return new CallToolResult
                {
                    Content = new List<ToolContent> {
                        new ToolContent { Text = $"Error executing tool {toolName}: {ex.Message}" }
                    },
                    IsError = true
                };
            }
        }

        CallToolResult ListTools()
        {
            var tools = GetAvailableTools();
            var json = JsonSerializer.Serialize(tools, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = json } }
            };
        }

        CallToolResult InvokeLazy<T>(Lazy<T> lazy, string methodName, Dictionary<string, object>? arguments) where T : class
        {
            if (lazy == null)
                throw new ArgumentNullException(nameof(lazy));
            try
            {
                object? instance;
                try
                {
                    instance = lazy.Value;
                }
                catch (Exception ex)
                {
                    McpLogger.Exception(ex, "InvokeLazy: failed to construct lazy.Value");
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> { new ToolContent { Text = $"InvokeLazy construction error: {ex.Message}" } },
                        IsError = true
                    };
                }

                if (instance == null)
                {
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> { new ToolContent { Text = "InvokeLazy: instance is null" } },
                        IsError = true
                    };
                }

                var type = instance.GetType();
                var mi = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (mi == null)
                {
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> { new ToolContent { Text = $"Method not found on {type.FullName}: {methodName}" } },
                        IsError = true
                    };
                }

                var parameters = mi.GetParameters();
                object?[] invokeArgs;

                if (parameters.Length == 0) {
                    invokeArgs = Array.Empty<object?>();
                }
                else {
                    // Pass arguments (null or empty dict) — the callee validates required params itself
                    invokeArgs = new object?[] { arguments };
                }

                var res = mi.Invoke(instance, invokeArgs);
                if (res is CallToolResult ctr)
                    return ctr;

                var text = JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true });
                return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = text } } };
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                McpLogger.Exception(inner, $"InvokeLazy error in {methodName}");
                return new CallToolResult
                {
                    Content = new List<ToolContent> { new ToolContent { Text = $"InvokeLazy invocation error: {inner.Message}" } },
                    IsError = true
                };
            }
            catch (Exception ex)
            {
                McpLogger.Exception(ex, "InvokeLazy failed");
                return new CallToolResult
                {
                    Content = new List<ToolContent> { new ToolContent { Text = $"InvokeLazy error: {ex.Message}" } },
                    IsError = true
                };
            }
        }

        dnlib.DotNet.AssemblyDef? FindAssemblyByName(string name)
        {
            return documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .FirstOrDefault(a => a?.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        }

        dnlib.DotNet.TypeDef? FindTypeInAssembly(dnlib.DotNet.AssemblyDef assembly, string typeFullName)
        {
            return assembly.Modules
                .SelectMany(m => m.Types)
                .FirstOrDefault(t => t.FullName.Equals(typeFullName, StringComparison.OrdinalIgnoreCase));
        }

        CallToolResult SearchTypes(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("query", out var queryObj))
                throw new ArgumentException("query is required");

            var query = queryObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            bool hasWildcard = query.Contains("*");
            System.Text.RegularExpressions.Regex? regex = null;

            if (hasWildcard)
            {
                var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(query).Replace("\\*", ".*") + "$";
                regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            var results = documentTreeView.GetAllModuleNodes()
                .SelectMany(m => m.Document?.AssemblyDef != null ? m.Document.AssemblyDef.Modules.SelectMany(mod => mod.Types) : Enumerable.Empty<dnlib.DotNet.TypeDef>())
                .Where(t => {
                    if (regex != null)
                        return regex.IsMatch(t.FullName);
                    return t.FullName.Contains(query, StringComparison.OrdinalIgnoreCase);
                })
                .Select(t => new
                {
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    AssemblyName = t.Module.Assembly?.Name.String ?? "Unknown"
                })
                .ToList();

            return CreatePaginatedJsonResponse(results, offset, pageSize);
        }

        CallToolResult FindWhoCallsMethod(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments are required");

            if (!arguments.TryGetValue("assembly_name", out var asmNameObj) ||
                !arguments.TryGetValue("type_full_name", out var typeNameObj) ||
                !arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("assembly_name, type_full_name, and method_name are required");

            var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {asmNameObj}");

            var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
            if (type == null)
                throw new ArgumentException($"Type not found: {typeNameObj}");

            var targetMethod = type.Methods.FirstOrDefault(m => m.Name.String == methodNameObj.ToString());
            if (targetMethod == null)
                throw new ArgumentException($"Method not found: {methodNameObj}");

            // Collect assembly references on the UI thread (documentTreeView is WPF-bound)
            var targetFullName = targetMethod.FullName;
            var assemblies = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                documentTreeView.GetAllModuleNodes()
                    .Select(m => m.Document?.AssemblyDef)
                    .Where(a => a != null)
                    .ToList());

            // IL scan runs on the background thread — dnlib objects are not WPF-bound
            var callers = assemblies
                .SelectMany(a => a!.Modules)
                .SelectMany(mod => GetAllTypesRecursive(mod))
                .SelectMany(t => t.Methods)
                .Where(m => m.Body?.Instructions != null)
                .SelectMany(m => m.Body.Instructions
                    .Where(instr =>
                        (instr.OpCode.Code == dnlib.DotNet.Emit.Code.Call ||
                         instr.OpCode.Code == dnlib.DotNet.Emit.Code.Callvirt) &&
                        instr.Operand is MethodDef calledDef && calledDef.FullName == targetFullName)
                    .Select(_ => new {
                        MethodName = m.Name.String,
                        DeclaringType = m.DeclaringType?.FullName ?? "Unknown",
                        AssemblyName = m.DeclaringType?.Module?.Assembly?.Name.String ?? "Unknown"
                    }))
                .OrderBy(c => c.AssemblyName).ThenBy(c => c.DeclaringType).ThenBy(c => c.MethodName)
                .ToList();

            var resultJson = System.Text.Json.JsonSerializer.Serialize(new {
                TargetMethod = targetFullName,
                CallerCount = callers.Count,
                Callers = callers
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult {
                Content = new List<ToolContent> { new ToolContent { Text = resultJson } }
            };
        }

        IEnumerable<TypeDef> GetAllTypesRecursive(ModuleDef module)
        {
            foreach (var type in module.Types)
            {
                yield return type;
                foreach (var nested in GetAllNestedTypesRecursive(type))
                    yield return nested;
            }
        }

        IEnumerable<TypeDef> GetAllNestedTypesRecursive(TypeDef type)
        {
            foreach (var nested in type.NestedTypes)
            {
                yield return nested;
                foreach (var deep in GetAllNestedTypesRecursive(nested))
                    yield return deep;
            }
        }

        CallToolResult AnalyzeTypeInheritance(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments are required");

            if (!arguments.TryGetValue("assembly_name", out var asmNameObj) ||
                !arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("assembly_name and type_full_name are required");

            var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {asmNameObj}");

            var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
            if (type == null)
                throw new ArgumentException($"Type not found: {typeNameObj}");

            var baseClasses = new List<string>();
            var currentType = type.BaseType;
            while (currentType != null && currentType.FullName != "System.Object")
            {
                baseClasses.Add(currentType.FullName);
                var typeDef = currentType.ResolveTypeDef();
                currentType = typeDef?.BaseType;
            }

            var interfaces = type.Interfaces.Select(i => i.Interface.FullName).ToList();

            var result = JsonSerializer.Serialize(new
            {
                Type = type.FullName,
                BaseClasses = baseClasses,
                Interfaces = interfaces
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        static (int offset, int pageSize) DecodeCursor(string? cursor)
        {
            if (string.IsNullOrEmpty(cursor))
                return (0, 50);

            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(cursor));
                var parts = decoded.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var offset) && int.TryParse(parts[1], out var pageSize))
                    return (offset, pageSize);
            }
            catch { }
            return (0, 10);
        }

        static string EncodeCursor(int offset, int pageSize)
        {
            return System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{offset}:{pageSize}"));
        }

        static CallToolResult CreatePaginatedJsonResponse<T>(List<T> items, int offset, int pageSize)
        {
            var pagedItems = items.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < items.Count;

            var result = new Dictionary<string, object>
            {
                ["items"] = pagedItems,
                ["total_count"] = items.Count,
                ["returned_count"] = pagedItems.Count,
                ["offset"] = offset
            };

            if (hasMore)
                result["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = json } }
            };
        }
    }
}
