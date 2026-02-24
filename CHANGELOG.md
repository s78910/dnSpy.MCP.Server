# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] - 2026-02-24

### Added
- **Memory Dump Tools** (3 new tools):
  - `list_runtime_modules` — enumerate all .NET modules loaded in debugged processes with address, size, dynamic/in-memory flags
  - `dump_module_from_memory` — extract a .NET module from process memory; uses `IDbgDotNetRuntime.GetRawModuleBytes` first (best quality), falls back to `DbgProcess.ReadMemory`
  - `read_process_memory` — read raw bytes from any process address, returned as formatted hex dump + Base64
- **Previously-hidden tools now exposed** (4 tools):
  - `get_type_fields` — list/search fields with glob/regex pattern
  - `get_type_property` — detailed property info with getter/setter/attributes
  - `find_path_to_type` — BFS reference path from one type to another
  - `list_native_modules` — P/Invoke DLL imports grouped by native DLL name

### Improved
- **Regex/glob support** added to `list_types` (`name_pattern`), `list_methods_in_type` (`name_pattern`), `list_runtime_modules` (`name_filter`), and `search_types` (existing query upgraded to detect regex)
- **Default page size** raised from 10 → 50 across all paginated tools
- Tool descriptions updated to document pattern syntax (glob `*`/`?` vs regex `^/$`)

### Total tools: 38 (was 31)

---

## [1.1.0] - 2026-02-24

### Added
- **Edit Tools** (7): `decompile_type`, `change_member_visibility`, `rename_member`, `save_assembly`, `list_events_in_type`, `get_custom_attributes`, `list_nested_types`
- **Debug Tools** (9): `get_debugger_state`, `list_breakpoints`, `set_breakpoint`, `remove_breakpoint`, `clear_all_breakpoints`, `continue_debugger`, `break_debugger`, `stop_debugging`, `get_call_stack`
- `dnSpy.Contracts.Debugger.DotNet` project reference for `DbgDotNetBreakpointFactory`

---

## [1.0.0] - 2026-02-24

### Added
- Initial release of dnSpy MCP Server
- Model Context Protocol (MCP) server integration with dnSpy
- HTTP/SSE server on localhost:3100

### Core Capabilities
- **Assembly Discovery**: List and navigate loaded .NET assemblies
- **Type Inspection**: Analyze types, methods, properties, and fields
- **Code Decompilation**: Decompile methods to C# code
- **IL Inspection**: View IL instructions, bytes, and exception handlers
- **Usage Finding**: Track method callers via IL analysis
- **Inheritance Analysis**: Analyze type inheritance chains
- **Search**: Find types by name across all loaded assemblies

### Available Tools (15)
| Category | Tools |
|----------|-------|
| Assembly | `list_assemblies`, `get_assembly_info` |
| Type | `list_types`, `get_type_info`, `search_types` |
| Method | `decompile_method`, `list_methods_in_type`, `get_method_signature` |
| Property | `list_properties_in_type` |
| Analysis | `find_who_calls_method`, `analyze_type_inheritance` |
| IL | `get_method_il`, `get_method_il_bytes`, `get_method_exception_handlers` |
| Utility | `list_tools` |

### Technical Details
- **Protocol**: MCP 2024-11-05
- **Framework**: .NET Framework 4.8 & .NET 10.0
- **Transport**: HTTP/SSE

### Client Configuration Examples
```json
{
  "mcpServers": {
    "dnspy-mcp": {
      "type": "streamable-http",
      "url": "http://localhost:3100",
      "alwaysAllow": ["list_assemblies", "list_tools"],
      "disabled": false
    }
  }
}
```
