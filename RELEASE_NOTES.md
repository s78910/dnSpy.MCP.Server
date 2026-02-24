# dnSpy MCP Server - Release v1.0.0

## Overview
This is the initial release of dnSpy MCP Server, a Model Context Protocol (MCP) server that exposes dnSpy's .NET assembly analysis capabilities to AI assistants.

## What's New

### First Release
- ✅ MCP server integration with dnSpy
- ✅ 15 tools for assembly analysis
- ✅ HTTP/SSE protocol support
- ✅ Multi-target: .NET Framework 4.8 + .NET 10.0

## Installation

### Prerequisites
- dnSpy (latest version)
- .NET Framework 4.8 SDK or .NET 10.0 SDK

### Steps
1. Copy the compiled DLL to your dnSpy extensions folder:
   - For .NET Framework 4.8: `dnSpy/dnSpy/bin/Release/net48/dnSpy.MCP.Server.x.dll`
   - For .NET 10.0: `dnSpy/dnSpy/bin/Release/net10.0-windows/dnSpy.MCP.Server.x.dll`

2. Start dnSpy - the MCP server will automatically start on `http://localhost:3100`

3. Configure your MCP client to connect to the server

## MCP Client Configuration

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

## Available Tools

### Assembly Tools
- `list_assemblies` - List all loaded assemblies
- `get_assembly_info` - Get detailed assembly information

### Type Tools
- `list_types` - List all types in an assembly
- `get_type_info` - Get detailed type information
- `search_types` - Search for types by name

### Method Tools
- `decompile_method` - Decompile a method to C#
- `list_methods_in_type` - List all methods in a type
- `get_method_signature` - Get method signature

### Property Tools
- `list_properties_in_type` - List all properties in a type

### Analysis Tools
- `find_who_calls_method` - Find methods that call a specific method
- `analyze_type_inheritance` - Analyze type inheritance chain

### IL Tools
- `get_method_il` - Get IL instructions
- `get_method_il_bytes` - Get raw IL bytes
- `get_method_exception_handlers` - Get exception handlers

### Utility Tools
- `list_tools` - List all available tools

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Extension not loading | Check output folder matches dnSpy location |
| Server won't start | Port 3100 in use? Check `netstat -ano \| findstr :3100` |
| Command not found | Call `list_tools` to verify available commands |
| Type not found | Use `list_assemblies` then `search_types` |

## License
This project is licensed under the GNU General Public License v3.0. See LICENSE file for details.
