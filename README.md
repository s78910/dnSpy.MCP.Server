# dnSpy MCP Server

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server embedded in dnSpy that exposes full .NET assembly analysis, editing, debugging, memory-dump, and deobfuscation capabilities to any MCP-compatible AI assistant.

**Version**: 1.3.0 | **Tools**: 67 | **Status**: ✅ 0 errors, 0 warnings | **Targets**: .NET 4.8 + .NET 10.0-windows

---

## Table of Contents

1. [Features](#features)
2. [Build & Install](#build--install)
3. [Client Configuration](#client-configuration)
4. [Tool Reference](#tool-reference)
   - [Assembly Tools](#assembly-tools)
   - [Type & Member Tools](#type--member-tools)
   - [Method & Decompilation Tools](#method--decompilation-tools)
   - [IL Tools](#il-tools)
   - [Analysis & Cross-Reference Tools](#analysis--cross-reference-tools)
   - [Edit Tools](#edit-tools)
   - [Debug Tools](#debug-tools)
   - [Memory Dump & PE Tools](#memory-dump--pe-tools)
   - [Static PE Analysis](#static-pe-analysis)
   - [Deobfuscation Tools](#deobfuscation-tools)
   - [Utility](#utility)
5. [Pattern Syntax](#pattern-syntax)
6. [Pagination](#pagination)
7. [Usage Examples](#usage-examples)
8. [Architecture](#architecture)
9. [Project Structure](#project-structure)
10. [Configuration](#configuration)
11. [Troubleshooting](#troubleshooting)

---

## Features

| Category | Capabilities |
|----------|-------------|
| **Assembly** | List loaded assemblies, namespaces, type counts, P/Invoke imports |
| **Types** | Inspect types, fields, properties, events, nested types, attributes, inheritance |
| **Decompilation** | Decompile entire types or individual methods to C# |
| **IL** | View IL instructions, raw bytes, local variables, exception handlers |
| **Analysis** | Find callers/users, trace field reads/writes, call graphs, dead code, cross-assembly dependencies |
| **Edit** | Rename members, change access modifiers, edit metadata, patch methods, inject types, save to disk |
| **Debug** | Manage breakpoints, launch/attach processes, pause/resume/stop sessions, inspect call stacks, read locals |
| **Memory Dump** | List runtime modules, dump .NET or native modules from memory, read process memory, extract PE sections |
| **Static PE Analysis** | Scan raw PE bytes for strings; all-in-one ConfuserEx unpacker |
| **Deobfuscation** | de4dot integration: detect obfuscator, rename mangled symbols, decrypt strings (.NET 4.8 only) |
| **Search** | Glob and regex search across all loaded assemblies |

---

## Build & Install

### Prerequisites

| Requirement | Version |
|-------------|---------|
| .NET SDK | 10.0+ (for net10.0-windows target) |
| .NET Framework SDK | 4.8 (for net48 target) |
| OS | Windows (WPF dependency) |
| dnSpy | dnSpyEx (this repo) |

> **de4dot integration** — de4dot libraries are bundled in `libs/de4dot/` (net48) and `libs/de4dot-net8/` (net8/net10). No external dependencies required; all deobfuscation tools are available in both build targets.

### Clone & Restore

```bash
git clone https://github.com/dnSpyEx/dnSpy --recursive
cd dnSpy
```

### Build commands

```bash
# Build only the MCP Server extension (Debug)
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Debug

# Build only the MCP Server extension (Release)
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release

# Build the full dnSpy solution (both targets)
dotnet build dnSpy.sln -c Debug

# Build for a specific target framework only
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release -f net10.0-windows
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release -f net48

# Restore NuGet packages without building
dotnet restore Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj

# Clean build artifacts
dotnet clean Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj
```

### Output locations

| Target | Output path |
|--------|-------------|
| .NET 10.0-windows | `dnSpy/dnSpy/bin/Release/net10.0-windows/dnSpy.MCP.Server.x.dll` |
| .NET Framework 4.8 | `dnSpy/dnSpy/bin/Release/net48/dnSpy.MCP.Server.x.dll` |

> The MCP Server DLL is output directly into dnSpy's bin directory so it loads automatically when you start dnSpy.

### Verify the build

```bash
# Check for errors (expects "Compilación correcta" or "Build succeeded")
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release --nologo 2>&1 | tail -5
```

### Runtime

1. Start dnSpy — the MCP server starts automatically on `http://localhost:3100`
2. Verify it is running: `curl http://localhost:3100/health` or open in a browser
3. Configure your MCP client (see next section)

---

## Client Configuration

The server uses the **Streamable HTTP** MCP transport. Add the following snippet to your client's MCP configuration file.

### Claude Code / Claude Desktop

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "streamable-http",
      "url": "http://localhost:3100"
    }
  }
}
```

### OpenCode

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "streamable-http",
      "url": "http://localhost:3100"
    }
  }
}
```

### Kilo Code / Roo Code

```json
{
  "mcpServers": {
    "dnspy-mcp": {
      "type": "streamable-http",
      "url": "http://localhost:3100",
      "alwaysAllow": [
        "list_assemblies", "list_tools", "search_types",
        "get_type_info", "list_methods_in_type"
      ],
      "disabled": false
    }
  }
}
```

### Codex CLI

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "streamable-http",
      "url": "http://localhost:3100",
      "timeout": 30
    }
  }
}
```

### Gemini CLI

```yaml
mcpServers:
  dnspy:
    type: streamable-http
    url: http://localhost:3100
```

---

## Tool Reference

All tools are called over MCP as `tools/call` with a JSON arguments object.
Parameters marked **required** must always be provided; all others are optional.

---

### Assembly Tools

Tools for listing and inspecting .NET assemblies loaded in dnSpy.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `list_assemblies` | List every assembly currently open in dnSpy, with version, culture, and public key token | — | — |
| `get_assembly_info` | Detailed info for one assembly: modules, namespaces, type count | `assembly_name` | `cursor` |
| `list_types` | List types in an assembly, with class/interface/enum flags. Supports glob and regex via `name_pattern` | `assembly_name` | `namespace`, `name_pattern`, `cursor` |
| `list_native_modules` | List native DLLs imported via `[DllImport]`, grouped by DLL name with the managed methods that use them | `assembly_name` | — |
| `load_assembly` | Load a .NET assembly into dnSpy from a file on disk **or** from a running process by PID. Supports both normal PE layout and raw memory-layout dumps | — | `file_path`, `memory_layout`, `pid`, `module_name` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `assembly_name` | string | Short assembly name as shown in dnSpy (e.g. `UnityEngine.CoreModule`) |
| `namespace` | string | Exact namespace filter (e.g. `System.Collections.Generic`) |
| `name_pattern` | string | Glob or regex filter on type name — see [Pattern Syntax](#pattern-syntax) |
| `cursor` | string | Opaque base-64 pagination cursor from `nextCursor` in a previous response |
| `file_path` | string | (`load_assembly`) Absolute path to a .NET assembly or memory dump |
| `memory_layout` | boolean | (`load_assembly`) When `true`, treat the file as raw memory-layout (VAs, not file offsets). Default `false` |
| `pid` | integer | (`load_assembly`) PID of a running .NET process to dump from. Requires active debug session. |
| `module_name` | string | (`load_assembly`) Module name/filename to pick when using `pid`. Defaults to first EXE module. |

---

### Type & Member Tools

Tools for inspecting the internals of a specific type.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_type_info` | Full type overview: visibility, base type, interfaces, fields, properties, methods (paginated) | `assembly_name`, `type_full_name` | `cursor` |
| `search_types` | Search for types by name across **all** loaded assemblies | `query` | `cursor` |
| `get_type_fields` | List fields matching a name pattern, with type, visibility, and `readonly`/`const` flags | `assembly_name`, `type_full_name`, `pattern` | `cursor` |
| `get_type_property` | Full detail for a single property: getter/setter signatures, attributes | `assembly_name`, `type_full_name`, `property_name` | — |
| `list_properties_in_type` | Summary list of all properties with read/write flags | `assembly_name`, `type_full_name` | `cursor` |
| `list_events_in_type` | All events with `add`/`remove` method info | `assembly_name`, `type_full_name` | — |
| `list_nested_types` | All nested types recursively (full name, visibility, kind) | `assembly_name`, `type_full_name` | — |
| `get_custom_attributes` | Custom attributes on the type or on one of its members | `assembly_name`, `type_full_name` | `member_name`, `member_kind` |
| `analyze_type_inheritance` | Full inheritance chain (base classes + interfaces) | `assembly_name`, `type_full_name` | — |
| `find_path_to_type` | BFS traversal of property/field references to find how one type reaches another | `assembly_name`, `from_type`, `to_type` | `max_depth` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `type_full_name` | string | Fully-qualified type name (e.g. `MyNamespace.MyClass`) |
| `query` | string | Substring, glob, or regex matched against `FullName` |
| `pattern` | string | Glob or regex for field name matching; use `*` to list all |
| `property_name` | string | Exact property name (case-insensitive) |
| `member_name` | string | Member name for attribute lookup (omit to get type-level attributes) |
| `member_kind` | string | Disambiguates overloaded names: `method`, `field`, `property`, or `event` |
| `from_type` | string | Full name of starting type for BFS path search |
| `to_type` | string | Name or substring of the target type |
| `max_depth` | integer | BFS depth limit (default `5`) |

---

### Method & Decompilation Tools

Tools for decompiling code and exploring method metadata.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `decompile_type` | Decompile an entire type (class/struct/interface/enum) to C# | `assembly_name`, `type_full_name` | — |
| `decompile_method` | Decompile a single method to C# | `assembly_name`, `type_full_name`, `method_name` | — |
| `list_methods_in_type` | List methods with return type, visibility, static, virtual, parameter count. Filter by visibility or name pattern | `assembly_name`, `type_full_name` | `visibility`, `name_pattern`, `cursor` |
| `get_method_signature` | Full signature for one method: parameters, return type, generic constraints | `assembly_name`, `type_full_name`, `method_name` | — |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `method_name` | string | Method name (first match when overloads exist; use `get_method_signature` to disambiguate) |
| `visibility` | string | Filter: `public`, `private`, `protected`, or `internal` |
| `name_pattern` | string | Glob or regex on method name (e.g. `Get*`, `^On[A-Z]`, `Async$`) |

---

### IL Tools

Low-level IL inspection for a method body.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_method_il` | IL instruction listing with offsets, opcodes, operands, and local variable table | `assembly_name`, `type_full_name`, `method_name` | — |
| `get_method_il_bytes` | Raw IL bytes as a hex string and Base64 | `assembly_name`, `type_full_name`, `method_name` | — |
| `get_method_exception_handlers` | try/catch/finally/fault region table (offsets and handler type) | `assembly_name`, `type_full_name`, `method_name` | — |

---

### Analysis & Cross-Reference Tools

Call-graph, usage, and dependency analysis across all loaded assemblies.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `find_who_calls_method` | Find every method whose IL contains a `call`/`callvirt` to the target | `assembly_name`, `type_full_name`, `method_name` | — |
| `find_who_uses_type` | Find all types, methods, and fields that reference a type (base class, interface, field type, parameter, return type) | `assembly_name`, `type_full_name` | — |
| `find_who_reads_field` | Find all methods that read a field via `ldfld`/`ldsfld` IL instructions | `assembly_name`, `type_full_name`, `field_name` | — |
| `find_who_writes_field` | Find all methods that write to a field via `stfld`/`stsfld` IL instructions | `assembly_name`, `type_full_name`, `field_name` | — |
| `analyze_call_graph` | Build a recursive call graph for a method, showing all methods it calls down to a configurable depth | `assembly_name`, `type_full_name`, `method_name` | `max_depth` |
| `find_dependency_chain` | Find all dependency paths between two types via BFS over base types, interfaces, fields, parameters, and return types | `assembly_name`, `from_type`, `to_type` | `max_depth` |
| `analyze_cross_assembly_dependencies` | Compute a dependency matrix for all loaded assemblies, showing which assemblies each depends on | — | — |
| `find_dead_code` | Identify methods and types never called or referenced (static approximation; virtual dispatch and reflection are not tracked) | `assembly_name` | — |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `field_name` | string | Exact field name to search for read/write access |
| `max_depth` | integer | Recursion depth limit for call-graph or BFS traversal (default `5`) |

---

### Edit Tools

In-memory metadata editing. Changes are applied immediately to dnlib's in-memory model and persist until `save_assembly` is called or dnSpy is closed without saving.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `change_member_visibility` | Change the access modifier of a type or one of its members | `assembly_name`, `type_full_name`, `member_kind`, `new_visibility` | `member_name` |
| `rename_member` | Rename a type or one of its members | `assembly_name`, `type_full_name`, `member_kind`, `old_name`, `new_name` | — |
| `save_assembly` | Write the (possibly modified) assembly to disk using dnlib's `ModuleWriter` | `assembly_name` | `output_path` |
| `get_assembly_metadata` | Read assembly-level metadata: name, version, culture, public key, flags, hash algorithm, module count, custom attributes | `assembly_name` | — |
| `edit_assembly_metadata` | Edit assembly-level metadata fields: name, version, culture, or hash algorithm | `assembly_name` | `name`, `version`, `culture`, `hash_algorithm` |
| `set_assembly_flags` | Set or clear an individual assembly attribute flag (e.g. `PublicKey`, `Retargetable`, processor architecture) | `assembly_name`, `flag_name`, `value` | — |
| `list_assembly_references` | List all assembly references (AssemblyRef table entries) in the manifest module | `assembly_name` | — |
| `add_assembly_reference` | Add an assembly reference by loading a DLL from disk. Creates a TypeForwarder to anchor the reference | `assembly_name`, `dll_path` | — |
| `inject_type_from_dll` | Deep-clone a type (fields, methods with IL, properties, events) from an external DLL into the target assembly | `assembly_name`, `dll_path`, `type_full_name` | — |
| `list_pinvoke_methods` | List all P/Invoke (`DllImport`) declarations in a type: managed name, token, DLL name, native function name | `assembly_name`, `type_full_name` | — |
| `patch_method_to_ret` | Replace a method's IL body with a minimal return stub (`nop` + `ret`) to neutralize it. Works on P/Invoke methods too (converts to managed stub) | `assembly_name`, `type_full_name`, `method_name` | — |

#### Parameter details

| Parameter | Type | Values / Description |
|-----------|------|---------------------|
| `member_kind` | string | `type`, `method`, `field`, `property`, or `event` |
| `new_visibility` | string | `public`, `private`, `protected`, `internal`, `protected_internal`, `private_protected` |
| `old_name` | string | Current member name |
| `new_name` | string | Desired new name |
| `output_path` | string | Absolute path for output file. Defaults to the original file location. |
| `flag_name` | string | Assembly flag to toggle (e.g. `PublicKey`, `Retargetable`, `PA_MSIL`, `PA_x86`, `PA_AMD64`) |
| `value` | boolean | `true` to set the flag, `false` to clear it |
| `dll_path` | string | Absolute path to the source DLL |

> **Note**: `rename_member` changes only the metadata name. It does **not** update call sites, string literals, or XML docs.

> **Note**: `patch_method_to_ret` is ideal for disabling anti-debug, anti-tamper, or license-check routines before saving and re-analyzing.

---

### Debug Tools

Interact with dnSpy's integrated debugger. Most tools require an active debug session.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `get_debugger_state` | Current state: `IsDebugging`, `IsRunning`, process list with thread/runtime counts | — | — |
| `list_breakpoints` | All registered code breakpoints with enabled state, bound count, and location | — | — |
| `set_breakpoint` | Set a breakpoint at a method entry point or specific IL offset | `assembly_name`, `type_full_name`, `method_name` | `il_offset` |
| `remove_breakpoint` | Remove a specific breakpoint | `assembly_name`, `type_full_name`, `method_name` | `il_offset` |
| `clear_all_breakpoints` | Remove every visible breakpoint | — | — |
| `continue_debugger` | Resume all paused processes (`RunAll`) | — | — |
| `break_debugger` | Pause all running processes (`BreakAll`) | — | — |
| `stop_debugging` | Terminate all active debug sessions | — | — |
| `get_call_stack` | Call stack of the currently selected (or first paused) thread — up to 50 frames | — | — |
| `start_debugging` | Launch an EXE under the dnSpy debugger. By default breaks at `EntryPoint` (after the module `.cctor` has run, so ConfuserEx-decrypted bodies are in RAM) | `exe_path` | `arguments`, `working_directory`, `break_kind` |
| `attach_to_process` | Attach the dnSpy debugger to a running .NET process by PID | `pid` | — |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `il_offset` | integer | IL byte offset within the method body (default `0` = method entry) |
| `exe_path` | string | Absolute path to the EXE to launch |
| `arguments` | string | Command-line arguments to pass to the process |
| `working_directory` | string | Working directory for the launched process |
| `break_kind` | string | `EntryPoint` (default, pauses after `.cctor`) or `ModuleCctorOrEntryPoint` (pauses before `.cctor`) |
| `pid` | integer | PID of the running .NET process to attach to |

> **Tip**: Use `start_debugging` + `break_kind: EntryPoint` for ConfuserEx-packed assemblies — method bodies are decrypted by the time the breakpoint hits. Then use `dump_module_from_memory` or `unpack_from_memory`.

---

### Memory Dump & PE Tools

Extract raw bytes from a debugged process. Requires an active debug session unless otherwise noted.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `list_runtime_modules` | Enumerate all .NET modules loaded in the debugged processes with address, size, `IsDynamic`, `IsInMemory`, and AppDomain | — | `process_id`, `name_filter` |
| `dump_module_from_memory` | Extract a .NET module from process memory to a file (preserves file layout when possible) | `module_name`, `output_path` | `process_id` |
| `read_process_memory` | Read up to 64 KB from any process address; returns a formatted hex dump and Base64 | `address`, `size` | `process_id` |
| `get_local_variables` | Read local variables and parameters from a paused stack frame; returns primitives, strings, and addresses for complex objects | — | `frame_index`, `process_id` |
| `get_pe_sections` | List PE section headers of a module in process memory (names, virtual addresses, sizes, characteristics) | `module_name` | `process_id` |
| `dump_pe_section` | Extract a specific PE section (e.g. `.text`, `.data`, `.rsrc`) from a module in process memory; writes to file and/or returns Base64 | `module_name`, `section_name` | `output_path`, `process_id` |
| `dump_module_unpacked` | Dump a full module with memory-to-file layout conversion (produces a valid loadable PE). Handles .NET, native, and mixed-mode modules | `module_name`, `output_path` | `process_id` |
| `dump_memory_to_file` | Save a contiguous range of process memory to a file. Supports up to 256 MB | `address`, `size`, `output_path` | `process_id` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `process_id` | integer | Target process ID (use `get_debugger_state` to find it). Defaults to the first paused process. |
| `name_filter` | string | Glob or regex filter on module name or filename |
| `module_name` | string | Module name, full filename, or basename (e.g. `MyApp.dll`). Use `list_runtime_modules` to find exact names. |
| `output_path` | string | Absolute path where the dumped bytes will be written. Parent directories are created automatically. |
| `address` | string | Memory address in hex (`0x7FF000`) or decimal |
| `size` | integer | Number of bytes to read/dump |
| `frame_index` | integer | Stack frame index (0 = top/innermost, default `0`) |
| `section_name` | string | PE section name (e.g. `.text`, `.data`, `.rsrc`) |

> **Layout note**: `dump_module_from_memory` reports `IsFileLayout` in its response. If `false`, use `dump_module_unpacked` instead for a corrected PE layout.

---

### Static PE Analysis

Tools that operate on raw PE file bytes — no debug session required.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `scan_pe_strings` | Scan raw PE file bytes for printable ASCII and UTF-16 strings. Useful for finding URLs, API keys, IP addresses, and embedded plaintext in packed/obfuscated assemblies | `assembly_name` | `min_length`, `encoding` |
| `unpack_from_memory` | All-in-one ConfuserEx unpacker: launches the EXE under the debugger (pausing at `EntryPoint` after decryption), dumps the main module with PE-layout fix, and optionally stops the session. Output can be loaded in dnSpy or passed to `deobfuscate_assembly` | `exe_path` | `output_path`, `stop_after_dump` |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `min_length` | integer | Minimum string length to include (default `4`) |
| `encoding` | string | `ascii`, `unicode`, or `both` (default `both`) |
| `exe_path` | string | Absolute path to the packed EXE to unpack |
| `output_path` | string | Destination for the unpacked PE (default: `<original_name>_unpacked.exe` next to the input) |
| `stop_after_dump` | boolean | Whether to stop the debug session after dumping (default `true`) |

> **Workflow**: `scan_pe_strings` → understand what the packed binary contains → `unpack_from_memory` → `deobfuscate_assembly` → load the clean file in dnSpy.

---

### Deobfuscation Tools

Integrated [de4dot](https://github.com/de4dot/de4dot) engine for symbol renaming and control-flow deobfuscation.

> **Requires**: .NET Framework 4.8 runtime (net48 dnSpy build). Not available on the net10.0-windows build.

| Tool | Description | Required params | Optional params |
|------|-------------|-----------------|-----------------|
| `list_deobfuscators` | List all obfuscator types supported by the de4dot engine (e.g. ConfuserEx, Dotfuscator, SmartAssembly, etc.) | — | — |
| `detect_obfuscator` | Detect which obfuscator was applied to a .NET assembly file on disk using de4dot's heuristic detection engine | `file_path` | — |
| `deobfuscate_assembly` | Deobfuscate a .NET assembly: renames mangled symbols, deobfuscates control flow, decrypts strings. Output is saved to disk | `file_path`, `output_path` | `obfuscator_type`, `rename_symbols` |
| `save_deobfuscated` | Return a previously deobfuscated file as a Base64-encoded blob. Useful when the output file cannot be accessed directly | `file_path` | — |

#### Parameter details

| Parameter | Type | Description |
|-----------|------|-------------|
| `file_path` | string | Absolute path to the .NET assembly on disk |
| `output_path` | string | Absolute path for the cleaned output assembly |
| `obfuscator_type` | string | Force a specific obfuscator (e.g. `cx` for ConfuserEx). Omit to let de4dot auto-detect. |
| `rename_symbols` | boolean | Whether to rename obfuscated symbols (default `true`) |

---

### Utility

| Tool | Description | Required params |
|------|-------------|-----------------|
| `list_tools` | Return the full schema for every registered tool as JSON | — |

---

## Pattern Syntax

Several tools accept a `name_pattern` or `query` parameter that supports both **glob** and **regex** syntax. The engine auto-detects the mode.

| Mode | Detected when | Examples |
|------|---------------|---------|
| **Glob** | Pattern contains only `*` or `?` wildcards | `Get*`, `*Controller`, `On?Click` |
| **Regex** | Pattern contains any of `^ $ [ ( \| + {` | `^Get[A-Z]`, `Controller$`, `^I[A-Z].*Service$` |
| **Substring** | No special characters (for `search_types` only) | `Player`, `Manager` |

All pattern matching is **case-insensitive**.

```
# Find all types whose name starts with "Player"
name_pattern: "Player*"

# Find all interfaces (start with I, followed by uppercase)
name_pattern: "^I[A-Z]"

# Find methods ending in "Async"
name_pattern: "Async$"

# Find all Get* or Set* methods
name_pattern: "^(Get|Set)[A-Z]"
```

---

## Pagination

List operations return paginated results. The default page size is **50 items**.

```json
{
  "items": [ ... ],
  "total_count": 312,
  "returned_count": 50,
  "nextCursor": "eyJvZmZzZXQiOjUwLCJwYWdlU2l6ZSI6NTB9"
}
```

To fetch the next page, pass the `nextCursor` value as the `cursor` argument in the next call. When `nextCursor` is absent, you have reached the last page.

---

## Usage Examples

### Workflow: explore an unknown assembly

```
1. list_assemblies                           → find "UnityEngine.CoreModule"
2. get_assembly_info  assembly=UnityEngine…  → see namespaces
3. list_types  assembly=…  namespace=UnityEngine  name_pattern="*Manager"
4. get_type_info  assembly=…  type=UnityEngine.NetworkManager
5. decompile_method  …  method=Awake
```

### Search with regex across all assemblies

```json
{ "tool": "search_types", "arguments": { "query": "^I[A-Z].*Repository$" } }
```

### Dump a Unity game module from memory

```json
{ "tool": "list_runtime_modules", "arguments": { "name_filter": "Assembly-CSharp*" } }

{ "tool": "dump_module_from_memory", "arguments": {
    "module_name": "Assembly-CSharp.dll",
    "output_path": "C:\\dump\\Assembly-CSharp_dump.dll"
}}
```

### Set a breakpoint and inspect the call stack

```json
{ "tool": "set_breakpoint", "arguments": {
    "assembly_name": "Assembly-CSharp",
    "type_full_name": "PlayerController",
    "method_name": "TakeDamage"
}}

{ "tool": "get_call_stack" }
```

### Change a private method to public and save

```json
{ "tool": "change_member_visibility", "arguments": {
    "assembly_name": "MyAssembly",
    "type_full_name": "MyNamespace.MyClass",
    "member_kind": "method",
    "member_name": "InternalHelper",
    "new_visibility": "public"
}}

{ "tool": "save_assembly", "arguments": {
    "assembly_name": "MyAssembly",
    "output_path": "C:\\patched\\MyAssembly.dll"
}}
```

### Read process memory at a known address

```json
{ "tool": "read_process_memory", "arguments": {
    "address": "0x7FFE00001000",
    "size": 256
}}
```

### Unpack a ConfuserEx-protected EXE and deobfuscate it

```
1. scan_pe_strings  assembly_name=MyApp  → confirm it's packed (few readable strings)
2. unpack_from_memory  exe_path=C:\MyApp.exe  output_path=C:\MyApp_unpacked.exe
3. detect_obfuscator  file_path=C:\MyApp_unpacked.exe  → identify remaining obfuscation
4. deobfuscate_assembly  file_path=C:\MyApp_unpacked.exe  output_path=C:\MyApp_clean.dll
```

### Patch anti-debug stubs and save a clean binary

```
1. list_pinvoke_methods  assembly=MyApp  type=AntiDebugClass
   → finds "CheckRemoteDebuggerPresent" → kernel32.dll
2. patch_method_to_ret  assembly=MyApp  type=AntiDebugClass  method=CheckRemoteDebuggerPresent
3. save_assembly  assembly=MyApp  output_path=C:\MyApp_patched.exe
```

### Load an assembly from disk or from a running process

```json
// Load a .NET DLL from disk
{ "tool": "load_assembly", "arguments": {
    "file_path": "C:\\dump\\MyApp_unpacked.dll"
}}

// Load a raw memory-layout dump (VAs instead of file offsets)
{ "tool": "load_assembly", "arguments": {
    "file_path": "C:\\dump\\MyApp_memdump.bin",
    "memory_layout": true
}}

// Dump from a running process and load directly into dnSpy
{ "tool": "load_assembly", "arguments": {
    "pid": 1234,
    "module_name": "MyPlugin.dll"
}}
```

### Find all callers and usages of a suspicious type

```json
{ "tool": "find_who_uses_type", "arguments": {
    "assembly_name": "MyAssembly",
    "type_full_name": "MyNamespace.ObfuscatedLicenseChecker"
}}

{ "tool": "find_who_writes_field", "arguments": {
    "assembly_name": "MyAssembly",
    "type_full_name": "MyNamespace.ObfuscatedLicenseChecker",
    "field_name": "isValid"
}}
```

---

## Architecture

| File | Responsibility |
|------|---------------|
| `src/Communication/McpServer.cs` | HTTP/SSE listener, JSON-RPC dispatch |
| `src/Application/McpTools.cs` | Central tool registry, schema definitions, routing |
| `src/Application/AssemblyTools.cs` | Assembly/type listing, P/Invoke analysis |
| `src/Application/TypeTools.cs` | Type detail, methods, IL, BFS path analysis |
| `src/Application/EditTools.cs` | Metadata editing, decompilation, assembly saving, method patching |
| `src/Application/DebugTools.cs` | Debugger state, breakpoints, stepping, process launch/attach |
| `src/Application/DumpTools.cs` | Runtime module enumeration, memory dump, PE section tools |
| `src/Application/MemoryInspectTools.cs` | Local variable inspection from paused debug frame |
| `src/Application/UsageFindingCommandTools.cs` | Cross-assembly IL usage analysis (callers, field reads/writes) |
| `src/Application/CodeAnalysisHelpers.cs` | Static call-graph, dependency chain, dead code analysis |
| `src/Application/De4dotTools.cs` | de4dot integration for deobfuscation (net48 only) |
| `src/Presentation/TheExtension.cs` | MEF entry point, server lifecycle |
| `src/Contracts/McpProtocol.cs` | MCP DTOs (ToolInfo, CallToolResult, …) |

---

## Project Structure

```
dnSpy.MCP.Server/
├── dnSpy.MCP.Server.csproj   # Multi-target: net48 + net10.0-windows
├── CHANGELOG.md
├── README.md
├── RELEASE_NOTES.md
└── src/
    ├── Application/
    │   ├── AssemblyTools.cs         # Assembly & type listing
    │   ├── TypeTools.cs             # Type internals + IL
    │   ├── EditTools.cs             # Metadata editing, method patching
    │   ├── DebugTools.cs            # Debugger integration
    │   ├── DumpTools.cs             # Memory dump & PE tools
    │   ├── MemoryInspectTools.cs    # Local variable inspection
    │   ├── UsageFindingCommandTools.cs  # IL usage analysis
    │   ├── CodeAnalysisHelpers.cs   # Call-graph & dependency analysis
    │   ├── De4dotTools.cs           # de4dot deobfuscation (net48)
    │   └── McpTools.cs              # Tool registry & routing
    ├── Communication/
    │   └── McpServer.cs             # HTTP/SSE server
    ├── Contracts/
    │   └── McpProtocol.cs           # DTO types
    ├── Helper/
    │   └── McpLogger.cs
    └── Presentation/
        ├── TheExtension.cs          # MEF export / entry point
        ├── ToolbarCommands.cs
        ├── McpSettings.cs
        └── McpSettingsPage.cs
```

---

## Configuration

### Port

Default port is **3100**. To change it, edit `src/Presentation/McpSettings.cs`:

```csharp
public const int DefaultPort = 3100;
```

Rebuild the project after changing this value.

### Verify the server is running

```bash
# Windows (PowerShell)
Invoke-RestMethod http://localhost:3100

# Windows (cmd)
curl http://localhost:3100

# Check port is listening
netstat -ano | findstr :3100
```

---

## Troubleshooting

| Symptom | Likely cause | Solution |
|---------|-------------|----------|
| Extension not loading | DLL not in dnSpy bin folder | Rebuild with `-c Release`; check output path in `.csproj` |
| `Connection refused` on port 3100 | Server failed to start | Check dnSpy's log window; port 3100 may be in use — `netstat -ano \| findstr :3100` |
| Tool returns `Unknown tool: …` | Name typo or outdated client cache | Call `list_tools` to see the current tool list |
| `Assembly not found` | Name mismatch | Call `list_assemblies` and use the exact `Name` value shown |
| `Type not found` | Wrong `type_full_name` | Use `list_types` or `search_types` to find the exact full name |
| `Debugger is not active` | No debug session running | Start debugging via `start_debugging` or dnSpy's **Debug** menu |
| `No paused process found` | Process is still running | Call `break_debugger` first |
| `dump_module_from_memory` returns no bytes | Module has no address (pure dynamic) | Some in-memory modules emitted by reflection emit cannot be dumped |
| Dump `IsFileLayout: false` | Memory layout dump | Use `dump_module_unpacked` instead — it performs the layout fix automatically |
| Deobfuscation tools not found | Running net10.0-windows build | de4dot tools only available in the net48 build; start the 32-bit dnSpy (`dnSpy.exe` not `dnSpy-x64.exe`) |
| `unpack_from_memory` fails with anti-debug error | Process kills itself before EntryPoint | Use `patch_method_to_ret` to neutralize anti-debug methods first, save the patched binary, then retry |

---

## Contributing

1. Fork or branch from `master`
2. Implement your changes under `src/Application/` or `src/Communication/`
3. Register new tools in `McpTools.cs` (`GetAvailableTools` + `ExecuteTool` switch)
4. Build with `dotnet build … --nologo` — must produce **0 errors, 0 warnings**
5. Manually test via any MCP client (`list_tools` to verify registration)
6. Submit a PR with a description of the new tool(s) and their parameters

---

## License

GNU General Public License v3.0 — see [LICENSE](LICENSE) for details.
