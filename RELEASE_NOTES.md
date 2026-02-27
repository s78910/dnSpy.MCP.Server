# dnSpy MCP Server — Release Notes

---

## v1.5.0 — 2026-02-27

### New: Window / Dialog Management Tools

Two new tools that let MCP clients detect and dismiss dialog boxes (error popups, confirmation dialogs) that appear inside the dnSpy process — previously impossible without direct UI interaction. Especially useful when a debug operation triggers an error MessageBox that freezes the session.

| Tool | Description |
|------|-------------|
| `list_dialogs` | Enumerate all active dialog/message-box windows (Win32 `#32770` + WPF). Returns title, HWND, message text, and button labels |
| `close_dialog` | Click a button on a dialog by name (EN/ES). Defaults to the first dialog and the OK button |

**Button matching** (case-insensitive): `ok`/`aceptar`, `yes`/`sí`, `no`, `cancel`/`cancelar`, `retry`/`reintentar`, `ignore`/`omitir`. Falls back to `WM_CLOSE` if no button matches.

### Implementation
- New file: `src/Application/WindowTools.cs` — P/Invoke only (`EnumWindows`, `EnumChildWindows`, `SendMessage`, `PostMessage`). No dnSpy service dependencies.
- `McpTools.cs` updated: field, constructor parameter, schema, and dispatch for both tools.

### Total tools: **87**

---

## v1.2.0 — 2026-02-24

### New: Memory Dump Tools

Three new tools to extract data from running processes.

| Tool | Description |
|------|-------------|
| `list_runtime_modules` | Enumerate all .NET modules in every debugged process, with base address, size, `IsDynamic`, `IsInMemory`, and AppDomain |
| `dump_module_from_memory` | Extract a .NET module from process memory to disk. Uses `IDbgDotNetRuntime.GetRawModuleBytes` (file layout, preferred) with automatic fallback to `DbgProcess.ReadMemory` |
| `read_process_memory` | Read raw bytes from any process address (1–65 536 bytes); returns a formatted hex dump and Base64 payload |

### Previously-hidden tools now exposed

Four tools that existed in the codebase but lacked MCP schemas and dispatch entries:

| Tool | Description |
|------|-------------|
| `get_type_fields` | List fields in a type with glob/regex pattern filtering |
| `get_type_property` | Detailed property info: getter/setter signatures and custom attributes |
| `find_path_to_type` | BFS traversal of property/field references to trace how one type reaches another |
| `list_native_modules` | All native DLLs imported via `[DllImport]`, grouped by DLL name |

### Improvements

- **Glob and regex support** added to `list_types` (`name_pattern`), `list_methods_in_type` (`name_pattern`), and `list_runtime_modules` (`name_filter`). Auto-detects regex (`^ $ [ ( | + {`) vs glob (`* ?`).
- **Default page size** raised from 10 → **50** items across all paginated tools.
- Tool descriptions updated to include pattern syntax examples.

### Total tools: **38**

---

## v1.1.0 — 2026-02-24

### New: Edit Tools (7)

| Tool | Description |
|------|-------------|
| `decompile_type` | Decompile an entire type to C# source |
| `change_member_visibility` | Change access modifier of a type or member |
| `rename_member` | Rename a type, method, field, property, or event |
| `save_assembly` | Save modified assembly to disk |
| `list_events_in_type` | List all events in a type |
| `get_custom_attributes` | Get custom attributes on a type or member |
| `list_nested_types` | Recursively list all nested types |

### New: Debug Tools (9)

| Tool | Description |
|------|-------------|
| `get_debugger_state` | Current debugger state and process list |
| `list_breakpoints` | All registered breakpoints |
| `set_breakpoint` | Add a breakpoint at a method/IL offset |
| `remove_breakpoint` | Remove a specific breakpoint |
| `clear_all_breakpoints` | Remove all breakpoints |
| `continue_debugger` | Resume all paused processes |
| `break_debugger` | Pause all running processes |
| `stop_debugging` | Stop all debug sessions |
| `get_call_stack` | Call stack of the current thread |

### Other
- Added `dnSpy.Contracts.Debugger.DotNet` project reference for `DbgDotNetBreakpointFactory`.
- Fixed `NativeModuleWriterOptions` to correctly cast `ModuleDef → ModuleDefMD`.

### Total tools: **31**

---

## v1.0.0 — 2026-02-24

### Initial release

15 tools covering assembly discovery, type/method inspection, decompilation, IL analysis, usage finding, and inheritance analysis.

| Category | Tools |
|----------|-------|
| Assembly | `list_assemblies`, `get_assembly_info` |
| Type | `list_types`, `get_type_info`, `search_types` |
| Method | `decompile_method`, `list_methods_in_type`, `get_method_signature` |
| Property | `list_properties_in_type` |
| Analysis | `find_who_calls_method`, `analyze_type_inheritance` |
| IL | `get_method_il`, `get_method_il_bytes`, `get_method_exception_handlers` |
| Utility | `list_tools` |

**Protocol**: MCP 2024-11-05 | **Transport**: HTTP/SSE | **Targets**: net48 + net10.0-windows
