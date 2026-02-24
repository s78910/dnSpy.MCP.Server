# dnSpy MCP Server

A Model Context Protocol (MCP) server that exposes dnSpy's .NET assembly analysis capabilities to AI assistants, enabling advanced code analysis, reverse engineering, and tool generation.

**Status**: 🟡 In Development | **Tools**: 15 Implemented | **Compilation**: ✅ 0 errors

---

## Features

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

---

## Installation & Build

### Prerequisites
- Visual Studio 2022 or dotnet CLI
- .NET Framework 4.8 SDK
- .NET 10.0 SDK

### Build Instructions

```bash
# Build the extension
dotnet build Extensions/dnSpy.MCP.Server/dnSpy.MCP.Server.csproj -c Release
```

**Output location:**
- .NET 4.8: `dnSpy/dnSpy/bin/Release/net48/dnSpy.MCP.Server.x.dll`
- .NET 10.0: `dnSpy/dnSpy/bin/Release/net10.0-windows/dnSpy.MCP.Server.x.dll`

### Runtime Setup

1. **Start dnSpy** with the compiled extension
2. **MCP Server** automatically starts on `http://localhost:3100`
3. **Configure MCP client**:

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

---

## Client Configuration

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

### Claude Code CLI

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

### Kilo Code

```json
{
  "mcpServers": {
    "dnspy-mcp": {
      "type": "streamable-http",
      "url": "http://localhost:3100",
      "alwaysAllow": ["list_assemblies", "list_tools", "search_types", "get_type_info"],
      "disabled": false
    }
  }
}
```

### Roo Code

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "streamable-http",
      "url": "http://localhost:3100",
      "alwaysAllow": ["list_assemblies", "list_tools"]
    }
  }
}
```

---

## Usage Examples

### List Loaded Assemblies
```python
client.call_tool("list_assemblies", {})
```

### Search for Types
```python
client.call_tool("search_types", {
    "query": "Player*"
})
```

### Get Type Info
```python
client.call_tool("get_type_info", {
    "assembly_name": "MyAssembly",
    "type_full_name": "Namespace.Player"
})
```

### Decompile Method
```python
client.call_tool("decompile_method", {
    "assembly_name": "MyAssembly",
    "type_full_name": "Namespace.Player",
    "method_name": "TakeDamage"
})
```

### Get IL Instructions
```python
client.call_tool("get_method_il", {
    "assembly_name": "MyAssembly",
    "type_full_name": "Namespace.Player",
    "method_name": "TakeDamage"
})
```

### Find Method Callers
```python
client.call_tool("find_who_calls_method", {
    "assembly_name": "MyAssembly",
    "type_full_name": "Namespace.Player",
    "method_name": "TakeDamage"
})
```

---

## Architecture

| Component | Purpose |
|-----------|---------|
| **McpServer.cs** | HTTP/SSE protocol handling |
| **McpTools.cs** | Tool definitions and routing |
| **AssemblyTools.cs** | Assembly operations |
| **TypeTools.cs** | Type/method analysis + IL tools |
| **UsageFindingCommandTools.cs** | IL-based caller analysis |

---

## Project Structure

```
dnSpy.MCP.Server/
├─ src/
│  ├─ Presentation/   # Settings and integration
│  ├─ Application/   # Tool implementations
│  ├─ Communication/  # HTTP server
│  ├─ Helper/        # Utilities
│  └─ Contracts/     # MCP DTOs
└─ README.md
```

---

## Configuration

### Port
Default port is **3100** (to avoid conflicts with Docker on port 3000).

To change, edit `src/Presentation/McpSettings.cs`:

```csharp
public const int DefaultPort = 3100;
```

---

## Compilation Status

✅ **0 Errors**
✅ **0 Warnings**
✅ **Multi-target**: .NET Framework 4.8 + .NET 10.0
✅ **MEF Composition**: Validated

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Extension not loading | Check output folder matches dnSpy location |
| Server won't start | Port 3100 in use? Check `netstat -ano \| findstr :3100` |
| Command not found | Call `list_tools` to verify available commands |
| Type not found | Use `list_assemblies` then `search_types` |

---

## Contributing

1. Create a new branch
2. Implement changes
3. Run `dotnet build` - must compile with 0 errors
4. Test via MCP client
5. Submit PR with clear description

---

## Development Info

**Language**: C# 12+
**Framework**: .NET Framework 4.8 & .NET 10.0
**Protocol**: Model Context Protocol (MCP) 2024-11-05
**Transport**: HTTP/SSE on localhost:3100

---

**Version**: 1.2 | **Status**: 🟡 Active Development
