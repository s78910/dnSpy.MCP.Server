/*
    Copyright (C) 2026 @chichicaste

    This file is part of dnSpy MCP Server module.

    dnSpy MCP Server is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy MCP Server is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy MCP Server.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.TreeView;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace dnSpy.MCP.Server.Application
{
    /// <summary>
    /// Provides run_script: execute arbitrary C# code via Roslyn inside dnSpy's process.
    ///
    /// Scripts have access to:
    ///   module       — ModuleDef? of the currently selected assembly (dnlib)
    ///   allModules   — IReadOnlyList&lt;ModuleDef&gt; of all loaded assemblies
    ///   print(...)   — append text to the output returned to the MCP caller
    ///   docService   — IDsDocumentService for loading/managing assemblies
    ///   dbgManager   — DbgManager? (null when no debug session is active)
    ///
    /// Standard namespaces pre-imported:
    ///   System, System.Linq, System.IO, System.Collections.Generic,
    ///   dnlib.DotNet, dnlib.DotNet.Emit, dnlib.DotNet.Writer
    ///
    /// The script return value (if any) is appended to the output as "Return: &lt;value&gt;".
    /// </summary>
    [Export(typeof(ScriptTools))]
    public sealed class ScriptTools
    {
        readonly IDsDocumentService documentService;
        readonly IDocumentTreeView documentTreeView;
        readonly Lazy<DbgManager> dbgManager;
        readonly Lazy<IDocumentTabService> documentTabService;

        [ImportingConstructor]
        public ScriptTools(
            IDsDocumentService documentService,
            IDocumentTreeView documentTreeView,
            Lazy<DbgManager> dbgManager,
            Lazy<IDocumentTabService> documentTabService)
        {
            this.documentService = documentService;
            this.documentTreeView = documentTreeView;
            this.dbgManager = dbgManager;
            this.documentTabService = documentTabService;
        }

        // ─────────────────────────────────────────────────────────────────────
        // run_script
        // ─────────────────────────────────────────────────────────────────────
        public string RunScript(Dictionary<string, object>? args)
        {
            if (!Configuration.McpConfig.Instance.EnableRunScript)
                throw new InvalidOperationException(
                    "run_script is disabled. Set \"enableRunScript\": true in mcp-config.json to enable.");

            string? code = null;
            if (args != null && args.TryGetValue("code", out var codeVal))
                code = codeVal?.ToString();
            if (string.IsNullOrEmpty(code))
                throw new ArgumentException("'code' is required.");
            int timeoutSeconds = (args != null && args.TryGetValue("timeout_seconds", out var to))
                ? Convert.ToInt32(to) : 30;

            var globals = BuildGlobals();
            var opts = BuildScriptOptions();

            var sb = globals.Output;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var task = CSharpScript.RunAsync(code!, opts, globals, typeof(McpScriptGlobals), cts.Token);
                task.Wait(cts.Token);

                var state = task.Result;
                var retVal = state.ReturnValue;
                if (retVal != null && retVal.GetType() != typeof(void))
                {
                    string retStr = retVal is System.Collections.IEnumerable en && !(retVal is string)
                        ? string.Join("\n", en.Cast<object>().Select(o => o?.ToString()))
                        : retVal.ToString()!;
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append("Return: ").AppendLine(retStr);
                }
            }
            catch (OperationCanceledException)
            {
                sb.AppendLine($"[!] Script timed out after {timeoutSeconds}s.");
            }
            catch (AggregateException aex)
            {
                var inner = aex.InnerException ?? aex;
                if (inner is CompilationErrorException cee)
                {
                    sb.AppendLine("[!] Compilation errors:");
                    foreach (var diag in cee.Diagnostics)
                        sb.AppendLine($"  {diag}");
                }
                else
                {
                    sb.AppendLine($"[!] Script error: {inner.GetType().Name}: {inner.Message}");
                    if (inner.StackTrace is string st)
                        sb.AppendLine(st.Split('\n').Take(6).Aggregate("", (a, b) => a + b + "\n"));
                }
            }

            return sb.Length == 0 ? "(no output)" : sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Globals object passed to every script
        // ─────────────────────────────────────────────────────────────────────
        McpScriptGlobals BuildGlobals()
        {
            // Resolve currently selected module
            ModuleDef? selectedModule = null;
            try
            {
                var node = documentTreeView.TreeView.SelectedItem as DocumentTreeNodeData;
                var doc = (node as DsDocumentNode)?.Document
                    ?? node?.GetAncestorOrSelf<DsDocumentNode>()?.Document;
                selectedModule = doc?.ModuleDef;
            }
            catch { /* no selection */ }

            var allMods = documentService.GetDocuments()
                .Select(d => d.ModuleDef)
                .Where(m => m != null)
                .ToList()!;

            DbgManager? dbg = null;
            try { dbg = dbgManager.Value; } catch { }

            return new McpScriptGlobals(selectedModule, allMods!, documentService, dbg);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ScriptOptions: references + default imports
        // ─────────────────────────────────────────────────────────────────────
        static ScriptOptions? _cachedOptions;
        static ScriptOptions BuildScriptOptions()
        {
            if (_cachedOptions != null) return _cachedOptions;

            // Collect assemblies to reference from the current AppDomain
            static Assembly? TryLoad(string name)
            {
                try { return Assembly.Load(name); } catch { return null; }
            }

            var refs = new List<Assembly?>
            {
                typeof(object).Assembly,                        // mscorlib / System.Runtime
                typeof(Enumerable).Assembly,                    // System.Linq
                typeof(File).Assembly,                          // System.IO
                typeof(JsonSerializer).Assembly,                // System.Text.Json
                typeof(ModuleDef).Assembly,                     // dnlib
                typeof(IDsDocumentService).Assembly,            // dnSpy.Contracts.DnSpy
                typeof(DbgManager).Assembly,                    // dnSpy.Contracts.Debugger
                TryLoad("System.Collections"),
                TryLoad("System.Text.RegularExpressions"),
                TryLoad("Newtonsoft.Json"),
                // Current assembly (MCP server) so scripts can access McpScriptGlobals
                typeof(ScriptTools).Assembly,
            };

            // Also add every already-loaded assembly — makes all dnSpy APIs available
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
                        refs.Add(asm);
                }
                catch { }
            }

            _cachedOptions = ScriptOptions.Default
                .WithReferences(refs.Where(r => r != null)!
                    .GroupBy(a => a!.FullName).Select(g => g.First()))
                .WithImports(
                    "System",
                    "System.Linq",
                    "System.IO",
                    "System.Text",
                    "System.Collections.Generic",
                    "System.Reflection",
                    "System.Threading",
                    "System.Threading.Tasks",
                    "System.Text.Json",
                    "dnlib.DotNet",
                    "dnlib.DotNet.Emit",
                    "dnlib.DotNet.Writer",
                    "dnSpy.Contracts.Documents",
                    "dnSpy.Contracts.Debugger");

            return _cachedOptions;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Globals class exposed to every script
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class McpScriptGlobals
    {
        // ── State ──────────────────────────────────────────────────────────
        internal readonly StringBuilder Output = new();

        // ── Script-visible globals ─────────────────────────────────────────

        /// <summary>Currently selected assembly's module, or null if none.</summary>
        public ModuleDef? module { get; }

        /// <summary>All loaded modules (all open assemblies).</summary>
        public IReadOnlyList<ModuleDef> allModules { get; }

        /// <summary>dnSpy document service — load/enumerate assemblies.</summary>
        public IDsDocumentService docService { get; }

        /// <summary>Active debugger manager, or null when no session is open.</summary>
        public DbgManager? dbgManager { get; }

        internal McpScriptGlobals(
            ModuleDef? module,
            IReadOnlyList<ModuleDef> allModules,
            IDsDocumentService docService,
            DbgManager? dbgManager)
        {
            this.module = module;
            this.allModules = allModules;
            this.docService = docService;
            this.dbgManager = dbgManager;
        }

        // ── Output helpers ─────────────────────────────────────────────────

        public void print(object? value = null) =>
            Output.AppendLine(value?.ToString() ?? "");

        public void print(string fmt, params object[] args) =>
            Output.AppendLine(string.Format(fmt, args));

        public void print(System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                Output.AppendLine(item?.ToString() ?? "(null)");
        }
    }
}
