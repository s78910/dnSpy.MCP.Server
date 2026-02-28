# dnSpy MCP Server - Architecture & Code Organization

## Overview

The dnSpy MCP Server implements a **Model Context Protocol (MCP)** server that exposes advanced .NET assembly analysis capabilities through an HTTP/SSE interface. The codebase is organized by functional domains to maintain clarity and enable incremental development.

---

## Core Components

### 1. **McpServer.cs**
**Responsibility**: HTTP/SSE Protocol Implementation
**Key Functionality**:
- HTTP listener on localhost:3100
- JSON-RPC 2.0 message handling
- SSE event streaming with 15-second heartbeats
- MCP lifecycle (initialize, ping, shutdown)
- Error handling and response formatting

**Public Methods**:
- `Start()` - Start HTTP listener
- `Stop()` - Graceful shutdown
- `ProcessRequest(stream)` - Handle MCP requests

---

### 2. **McpTools.cs** + **McpTools.Schemas.cs** (partial class)
**Responsibility**: Central tool registry and command dispatcher
**Structure**: Split into two files via C# `partial class`

#### **McpTools.cs** — Dispatch & Helpers
- `ExecuteTool(toolName, arguments)` — Main dispatcher switch (95 cases)
- `ListTools()` — Self-discovery endpoint
- `InvokeLazy<T>()` — Reflection-based delegation to lazy-loaded service classes
- `FindAssemblyByName()` / `FindTypeInAssembly()` — Assembly/type lookup helpers
- `EncodeCursor()` / `DecodeCursor()` — Cursor-based pagination state
- `CreatePaginatedJsonResponse<T>()` — Paginated result formatter
- `HandleGetMcpConfig()` / `HandleReloadMcpConfig()` — Config helpers
- `SearchTypes()`, `FindWhoCallsMethod()`, `AnalyzeTypeInheritance()` — Inline analysis helpers
- `GetAllTypesRecursive()` / `GetAllNestedTypesRecursive()` — IL traversal helpers

#### **McpTools.Schemas.cs** — Tool Registry (partial class extension)
- `GetAvailableTools()` — Aggregates 13 category methods into the full tool list
- 13 private category methods: `GetAssemblyToolSchemas()`, `GetTypeToolSchemas()`, `GetMethodILToolSchemas()`, `GetAnalysisToolSchemas()`, `GetEditToolSchemas()`, `GetResourceToolSchemas()`, `GetDebugToolSchemas()`, `GetMemoryToolSchemas()`, `GetDeobfuscationToolSchemas()`, `GetSkillsToolSchemas()`, `GetScriptingToolSchemas()`, `GetWindowToolSchemas()`, `GetUtilityToolSchemas()`

**Constructor** (MEF `[ImportingConstructor]`) injects 15 lazy services:
`assemblyTools`, `typeTools`, `editTools`, `resourceTools`, `debugTools`, `dumpTools`, `memoryInspectTools`, `usageFindingCommandTools`, `codeAnalysisHelpers`, `de4dotTools`, `skillsTools`, `scriptTools`, `windowTools`, plus `IDocumentTreeView` and `IDecompilerService`.

---

### 3. **AssemblyTools.cs** (Lazy-Loaded)
**Responsibility**: Assembly-focused Operations
**Methods**:
- `ListAssemblies()` - List loaded assemblies with metadata
- `GetAssemblyInfo()` - Assembly structure with pagination
- `ListTypes()` - Types by assembly/namespace with glob/regex filter
- `SelectAssembly()` / `CloseAssembly()` / `CloseAllAssemblies()` - Assembly lifecycle
- `ListNativeModules()` - Native module enumeration

**Integration**: Invoked via reflection through `InvokeLazy<AssemblyTools>()`

---

### 4. **TypeTools.cs** (Lazy-Loaded)
**Responsibility**: Type-focused Deep Analysis
**Methods**:
- `GetTypeInfo()` - Complete type information with pagination
- `DecompileMethod()` / `DecompileType()` - C# decompilation via dnSpy
- `ListMethodsInType()` / `ListPropertiesInType()` / `ListEventsInType()` - Member enumeration
- `GetTypeFields()` - Field pattern matching
- `GetTypeProperty()` - Property details
- `GetMethodSignature()` - Method signature analysis
- `SearchTypes()` - Type search with wildcards/regex
- `FindPathToType()` - BFS reference path between types
- `ListNestedTypes()` / `GetCustomAttributes()` - Type metadata

**Integration**: Invoked via reflection through `InvokeLazy<TypeTools>()`

---

### 5. **EditTools.cs** (Lazy-Loaded)
**Responsibility**: Assembly metadata editing and IL patching
**Methods**:
- `ChangeVisibility()` / `RenameMember()` - Member editing
- `SaveAssembly()` - Persist changes to disk
- `GetAssemblyMetadata()` / `EditAssemblyMetadata()` / `SetAssemblyFlags()` - Metadata tools
- `ListAssemblyReferences()` / `AddAssemblyReference()` / `RemoveAssemblyReference()` - AssemblyRef management
- `InjectTypeFromDll()` - Deep-clone type from external DLL into target
- `ListPInvokeMethods()` - Enumerate DllImport declarations
- `PatchMethodToRet()` - Replace method body with nop+ret stub

---

### 6. **ResourceTools.cs** (Lazy-Loaded)
**Responsibility**: Embedded resource management
**Methods**:
- `ListResources()` - Enumerate ManifestResource entries
- `GetResource()` - Extract resource as Base64 or save to disk
- `AddResource()` - Embed file as EmbeddedResource
- `RemoveResource()` - Remove resource from manifest
- `ExtractCostura()` - Extract Costura.Fody-embedded assemblies

---

### 7. **DebugTools.cs** (Lazy-Loaded)
**Responsibility**: Debugger lifecycle, breakpoint management, and single-step execution
**Methods**:
- `GetDebuggerState()` / `GetCallStack()` - Session inspection
- `ListBreakpoints()` / `SetBreakpoint()` / `RemoveBreakpoint()` / `ClearAllBreakpoints()` - Breakpoint CRUD (`SetBreakpoint` supports optional C# `condition` expression via `DbgCodeBreakpointCondition`)
- `ContinueDebugger()` / `BreakDebugger()` / `StopDebugging()` - Execution control
- `StepOver()` / `StepInto()` / `StepOut()` - Single-step; delegates to `StepImpl()` which posts `DbgStepper.Step(kind)` on the WPF Dispatcher and blocks the MCP thread on a `ManualResetEventSlim` until `StepComplete` fires
- `GetCurrentLocation()` - Read top-frame token/offset/module without stepping; runs on Dispatcher
- `WaitForPause()` - 100 ms polling loop until any process enters `Paused` state
- `StartDebugging()` - Launch .NET EXE under dnSpy debugger (configurable break kind)
- `AttachToProcess()` - Attach to running .NET process by PID

**Private helpers**:
- `ResolveThread(mgr, args)` - Resolve thread by explicit `thread_id`, `process_id` filter, current thread, or first paused thread
- `StepImpl(args, kind)` - Shared stepping logic with timeout and `StepComplete` async bridge

---

### 8. **DumpTools.cs** (Lazy-Loaded)
**Responsibility**: Memory dump, PE analysis, and live memory patching
**Methods**:
- `ListRuntimeModules()` - Enumerate .NET modules loaded in debugged process
- `DumpModuleFromMemory()` - Raw memory dump of .NET module
- `DumpModuleUnpacked()` - Dump with memory-to-file layout conversion (PE fix for IDA/dnSpy)
- `ReadProcessMemory()` - Raw bytes from any address (hex dump + Base64)
- `WriteProcessMemory()` - Write bytes to a process address via `DbgProcess.WriteMemory()`. Accepts base64 or hex-string input (normalised by `ParseHexBytes()`)
- `DumpMemoryToFile()` - Save memory range to disk (up to 256 MB)
- `GetPeSections()` - List PE sections with VA, size, characteristics
- `DumpPeSection()` - Extract a PE section (.text, .data, .rsrc, etc.)
- `ScanPeStrings()` - Scan PE bytes for ASCII/UTF-16 strings
- `UnpackFromMemory()` - All-in-one ConfuserEx unpacker (launch → dump → fix)

**Private helpers**:
- `ParseHexBytes(hex)` - Normalise hex-string input (`0x`/`0X` prefixes, spaces, commas, dashes) and parse byte array; net48-compatible
- `TryParseAddress(s, out ulong)` - Parse hex or decimal address string
- `BuildHexDump(bytes, baseAddr)` - Format bytes as annotated hex dump

---

### 9. **MemoryInspectTools.cs** (Lazy-Loaded)
**Responsibility**: Runtime variable inspection and expression evaluation in paused debug sessions
**Methods**:
- `GetLocalVariables()` - Read locals and parameters from current stack frame using `IDbgDotNetRuntime`
- `EvalExpression()` - Evaluate a C# expression in the current frame via `DbgLanguage.ExpressionEvaluator.Evaluate()`. Returns typed value (primitive, string, or object address) via `FormatDbgValue()` helper
- `DumpCordbgIl()` - Detect ConfuserEx JIT hooks via `ICorDebugFunction.ILCode.Address`

**Private helpers**:
- `FormatDbgValue(DbgValue?)` - Format base `DbgValue` result: checks `HasRawValue`/`ValueType` for primitives and strings; falls back to `GetRawAddressValue()` for objects
- `FormatValueResult(DbgDotNetValueResult, DbgProcess)` - Format richer `DbgDotNetValue` results for `GetLocalVariables`

---

### 10. **UsageFindingCommandTools.cs** (Lazy-Loaded)
**Responsibility**: IL-Based Usage Analysis
**Methods**:
- `FindWhoUsesType()` - Type reference tracing (base class, interface, field, parameter, return type)
- `FindWhoCallsMethod()` - Method call tracking via CALL/CALLVIRT
- `FindWhoReadsField()` - Field read analysis via LDFLD/LDSFLD
- `FindWhoWritesField()` - Field write analysis via STFLD/STSFLD

**Dependencies**:
- `IDocumentTreeView` - dnSpy service for assembly navigation
- `dnlib` IL analysis

---

### 11. **CodeAnalysisHelpers.cs** (Lazy-Loaded)
**Responsibility**: Advanced Code Analysis Infrastructure
**Methods**:
- `AnalyzeCallGraph()` - Recursive method call graph up to configurable depth
- `FindDependencyChain()` - BFS dependency path between two types
- `AnalyzeCrossAssemblyDependencies()` - Dependency matrix across all loaded assemblies
- `FindDeadCode()` - Unused methods and types (static reference approximation)

**Helpers** (shared):
- `GetAllTypesRecursive()` / `GetNestedTypesRecursive()` - Type tree traversal
- `IsTypeReferenced()` - Cross-assembly reference check

---

### 12. **De4dotTools.cs** (Lazy-Loaded)
**Responsibility**: Deobfuscation via integrated de4dot engine
**Methods**:
- `ListDeobfuscators()` - List all supported obfuscator types
- `DetectObfuscator()` - Heuristic obfuscator detection
- `DeobfuscateAssembly()` - Full deobfuscation with configurable rename/cflow/string flags
- `SaveDeobfuscated()` - Return deobfuscated file as Base64
- `RunDe4dot()` - Invoke `de4dot.exe` as external process

**Note**: de4dot DLLs bundled locally (`libs/de4dot/` for net48, `libs/de4dot-net8/` for net10). Available in both build targets.

---

### 13. **SkillsTools.cs** (Lazy-Loaded)
**Responsibility**: Persistent knowledge base for RE procedures and workflows
**Methods**:
- `ListSkills()` / `GetSkill()` / `SaveSkill()` / `SearchSkills()` / `DeleteSkill()` - CRUD
**Storage**: `%APPDATA%\dnSpy\dnSpy.MCPServer\skills\` — paired `{id}.md` + `{id}.json` files

---

### 14. **ScriptTools.cs** (Lazy-Loaded)
**Responsibility**: Roslyn C# scripting inside the dnSpy process
**Methods**:
- `RunScript(code)` - Execute arbitrary C# via `Microsoft.CodeAnalysis.CSharp.Scripting`

**Globals available in scripts**: `module`, `allModules`, `docService`, `dbgManager`, `print()`

---

### 15. **WindowTools.cs**
**Responsibility**: Win32 and WPF dialog enumeration and dismissal
**Methods**:
- `ListDialogs()` — Enumerate active dialog windows (Win32 `#32770` + WPF `Application.Current.Windows`). Returns title, HWND, message text (Static child controls), and button labels.
- `CloseDialog(args)` — Resolve target dialog by HWND (hex) or first found; send `BM_CLICK` to matching child button. Fallback: `WM_CLOSE`.

**Implementation details**:
- P/Invoke only: `EnumWindows`, `EnumChildWindows`, `GetWindowText`, `GetClassName`, `IsWindowVisible`, `GetWindowThreadProcessId`, `SendMessage`, `PostMessage`.
- No dnSpy service dependencies — `[ImportingConstructor] WindowTools() { }`.
- `WpfApp = System.Windows.Application` alias avoids namespace ambiguity with `dnSpy.MCP.Server.Application`.
- Button matching: exact tokens EN + ES → substring fallback.

---

## Supporting Infrastructure

### **McpProtocol.cs**
Data models for MCP messages:
- `McpRequest` / `McpResponse`
- `CallToolRequest` / `CallToolResult`
- `ToolInfo` / `ToolContent`
- `McpError`

### **McpLogger.cs**
Structured logging with levels:
- `Debug()` - Diagnostic information
- `Info()` - General information
- `Exception()` - Error tracking

### **McpSettings.cs**
Configuration management:
- Settings persistence via `mcp-config.json` in `%APPDATA%\dnSpy\dnSpy.MCPServer\`
- WPF settings UI
- Integration with dnSpy settings system
- Hot-reload via `reload_mcp_config` tool

### **StringBuilderDecompilerOutput** (McpTools.cs)
Helper class that captures decompiler output into a string for response formatting.

---

## Data Flow Diagram

```
Client Request (HTTP POST)
    ↓
McpServer.ProcessRequest()
    ↓
McpTools.ExecuteTool(toolName, args)
    ↓
┌─────────────────────────────────────────────────┐
│ Command Routing (switch statement — 95 cases)   │
├─────────────────────────────────────────────────┤
│                                                 │
├─ Inline Helpers (McpTools.cs)                   │
│  ├─ Find* commands (IL traversal)               │
│  ├─ AnalyzeTypeInheritance()                    │
│  └─ Config / pagination helpers                 │
│                                                 │
├─ Lazy Delegation (Reflection)                   │
│  ├─ InvokeLazy(assemblyTools, ...)              │
│  ├─ InvokeLazy(typeTools, ...)                  │
│  ├─ InvokeLazy(editTools, ...)                  │
│  ├─ InvokeLazy(resourceTools, ...)              │
│  ├─ InvokeLazy(debugTools, ...)                 │
│  ├─ InvokeLazy(dumpTools, ...)                  │
│  ├─ InvokeLazy(memoryInspectTools, ...)         │
│  ├─ InvokeLazy(usageFindingCommandTools, ...)   │
│  ├─ InvokeLazy(codeAnalysisHelpers, ...)        │
│  ├─ InvokeLazy(de4dotTools, ...)                │
│  ├─ InvokeLazy(skillsTools, ...)                │
│  ├─ InvokeLazy(scriptTools, ...)                │
│  └─ InvokeLazy(windowTools, ...)               │
│                                                 │
└─────────────────────────────────────────────────┘
    ↓
Resolve dnlib Types & Analyze
    ↓
Serialize Result (JSON)
    ↓
Return CallToolResult
    ↓
McpServer.SendResponse(JSON)
    ↓
Client (Formatted JSON Response)
```

---

## Initialization Flow

```
TheExtension (MEF Plugin)
    ↓
[ImportingConstructor]
McpTools(
    IDocumentTreeView, IDecompilerService,
    Lazy<AssemblyTools>, Lazy<TypeTools>, Lazy<EditTools>,
    Lazy<ResourceTools>, Lazy<DebugTools>, Lazy<DumpTools>,
    Lazy<MemoryInspectTools>, Lazy<UsageFindingCommandTools>,
    Lazy<CodeAnalysisHelpers>, Lazy<De4dotTools>,
    Lazy<SkillsTools>, Lazy<ScriptTools>, Lazy<WindowTools>
)
    ↓
McpServer(McpTools)
    ↓
McpServer.Start()
    ├─ Creates HttpListener on :3100
    ├─ Spawns background thread for request handling
    ├─ Starts SSE heartbeat timer (15s)
    └─ Ready to receive MCP requests
```

---

## Key Design Patterns

### 1. **Lazy Initialization**
All 13 service classes are wrapped in `Lazy<T>` to defer MEF construction until first use, reducing startup overhead.

### 2. **Reflection-Based Delegation**
`InvokeLazy<T>(lazy, methodName, arguments)` invokes a method on a lazy-loaded service by name. On `TargetInvocationException` the inner exception is logged and re-thrown, preserving the original stack trace.

### 3. **Partial Class Split**
`McpTools` is split across two files:
- `McpTools.cs` — ~370 lines: fields, constructor, `ExecuteTool()` switch, helpers
- `McpTools.Schemas.cs` — ~1250 lines: `GetAvailableTools()` + 13 `Get*ToolSchemas()` category methods

This keeps the dispatch logic and schema declarations independently editable.

### 4. **Pagination**
Commands handling large result sets use cursor-based pagination:
- `EncodeCursor(offset, pageSize)` → Base64-encoded JSON
- `DecodeCursor(cursor)` → (offset, pageSize) tuple
- `nextCursor` field in response signals more results

### 5. **IL Instruction Analysis**
Usage-finding commands use dnlib IL traversal to identify:
- **CALL / CALLVIRT** → Method invocations
- **LDFLD / LDSFLD** → Field reads
- **STFLD / STSFLD** → Field writes

---

## Command Categories

| Category | Representative Tools | Count | Status |
|----------|---------------------|-------|--------|
| Assembly | `list_assemblies`, `get_assembly_info`, `list_types`, `load_assembly`, `select_assembly`, `close_assembly`, `close_all_assemblies` | 7 | ✅ |
| Type | `get_type_info`, `search_types`, `decompile_method`, `decompile_type`, `list_methods_in_type`, `find_path_to_type` | 13 | ✅ |
| Method / IL | `get_method_il`, `get_method_il_bytes`, `get_method_exception_handlers`, `dump_cordbg_il` | 4 | ✅ |
| Analysis | `find_who_calls_method`, `find_who_uses_type`, `find_who_reads_field`, `find_who_writes_field`, `analyze_type_inheritance`, `analyze_call_graph`, `find_dependency_chain`, `find_dead_code` | 9 | ✅ |
| Edit | `change_member_visibility`, `rename_member`, `save_assembly`, `get/edit_assembly_metadata`, `set_assembly_flags`, `list/add/remove_assembly_reference`, `inject_type_from_dll`, `patch_method_to_ret` | 13 | ✅ |
| Resource | `list_resources`, `get_resource`, `add_resource`, `remove_resource`, `extract_costura` | 5 | ✅ |
| Debug | `get_debugger_state`, `list/set(+condition)/remove/clear_breakpoints`, `continue/break/stop_debugger`, `get_call_stack`, `step_over`, `step_into`, `step_out`, `get_current_location`, `wait_for_pause`, `start_debugging`, `attach_to_process` | 16 | ✅ |
| Memory / PE | `list_runtime_modules`, `dump_module_unpacked`, `read_process_memory`, `write_process_memory`, `get_pe_sections`, `dump_pe_section`, `scan_pe_strings`, `unpack_from_memory` | 10 | ✅ |
| Runtime Inspect | `get_local_variables`, `eval_expression` | 2 | ✅ |
| Deobfuscation | `list_deobfuscators`, `detect_obfuscator`, `deobfuscate_assembly`, `save_deobfuscated`, `run_de4dot` | 5 | ✅ |
| Skills | `list_skills`, `get_skill`, `save_skill`, `search_skills`, `delete_skill` | 5 | ✅ |
| Scripting | `run_script` | 1 | ✅ |
| Window / Dialog | `list_dialogs`, `close_dialog` | 2 | ✅ |
| Utility | `list_tools`, `get_mcp_config`, `reload_mcp_config` | 3 | ✅ |
| **Total** | | **95** | ✅ |

---

## Testing Strategy

### Manual Testing (Current)
- dnSpy UI testing with live assemblies
- MCP client integration testing
- Pagination cursor validation
- Error handling verification

### Future Testing
- Unit tests for helper methods (FindAssemblyByName, DecodeCursor, etc.)
- IL analysis validation against known assemblies
- Pagination boundary testing
- Integration tests with dnSpy service mocks

---

## Performance Considerations

### Current
- **No caching** - Results computed on-demand
- **Sequential IL analysis** - Single-threaded method enumeration
- **No indexing** - Full type/assembly scan per query

### Optimization Opportunities
- **Result caching** - Cache decompilation and IL analysis results
- **Parallel scanning** - Multi-threaded assembly enumeration
- **Index building** - Pre-compute reference graphs
- **Lazy loading** - Load results on demand for large result sets

---

## Integration with dnSpy

**Services Used**:
- `IDocumentTreeView` - Access to loaded assemblies/types
- `IDecompilerService` - C# decompilation capabilities
- `IAssemblyResolver` - Cross-assembly type resolution

**MEF Composition**:
- `[Export(typeof(McpTools))]` - Export tool provider
- `[ImportingConstructor]` - Dependency injection
- Automatic service discovery and wiring

---

## File Structure Summary

```
dnSpy.MCP.Server/
├─ src/
│  ├─ Presentation/   # UI integration (menus, settings UI)
│  └─ Application/    # Command handlers
│     ├─ McpTools.cs             # Dispatch + helpers (partial)
│     ├─ McpTools.Schemas.cs     # Tool schemas — 13 categories (partial)
│     ├─ AssemblyTools.cs
│     ├─ TypeTools.cs
│     ├─ EditTools.cs
│     ├─ ResourceTools.cs
│     ├─ DebugTools.cs
│     ├─ DumpTools.cs
│     ├─ MemoryInspectTools.cs
│     ├─ UsageFindingCommandTools.cs
│     ├─ CodeAnalysisHelpers.cs
│     ├─ De4dotTools.cs
│     ├─ SkillsTools.cs
│     ├─ ScriptTools.cs
│     └─ WindowTools.cs
├─ docs/
│  ├─ ARCHITECTURE.md
│  └─ STATUS.md
└─ README.md
```

---

## Document Version
- **Version**: 1.6
- **Updated**: 2026-02-27
- **Status**: Architecture documented for v1.6.0 — 95 tools, production ready
