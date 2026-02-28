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

// De4dotTools.cs — de4dot deobfuscation integration for dnSpy MCP Server
// Mirrors the tools in de4dot.mcp (detect_obfuscator, deobfuscate_assembly,
// list_deobfuscators, save_deobfuscated) but runs in-process inside dnSpy.
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using de4dot.code;
using de4dot.code.AssemblyClient;
using de4dot.code.deobfuscators;
using de4dot.code.renamer;
using de4dot.code.deobfuscators.Unknown;
using de4dot.code.deobfuscators.Agile_NET;
using de4dot.code.deobfuscators.Babel_NET;
using de4dot.code.deobfuscators.CodeFort;
using de4dot.code.deobfuscators.CodeVeil;
using de4dot.code.deobfuscators.CodeWall;
using de4dot.code.deobfuscators.Confuser;
using de4dot.code.deobfuscators.CryptoObfuscator;
using de4dot.code.deobfuscators.DeepSea;
using de4dot.code.deobfuscators.Dotfuscator;
using de4dot.code.deobfuscators.Eazfuscator_NET;
using de4dot.code.deobfuscators.Goliath_NET;
using de4dot.code.deobfuscators.ILProtector;
using de4dot.code.deobfuscators.MaxtoCode;
using de4dot.code.deobfuscators.MPRESS;
using de4dot.code.deobfuscators.Obfuscar;
using de4dot.code.deobfuscators.Rummage;
using de4dot.code.deobfuscators.Skater_NET;
using de4dot.code.deobfuscators.SmartAssembly;
using de4dot.code.deobfuscators.Spices_Net;
using de4dot.code.deobfuscators.Xenocode;
using dnlib.DotNet;

using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Application
{
    /// <summary>
    /// de4dot deobfuscation tools integrated into the dnSpy MCP Server.
    /// Provides detect_obfuscator, deobfuscate_assembly, list_deobfuscators,
    /// and save_deobfuscated commands — equivalent to de4dot.mcp but in-process.
    /// </summary>
    [Export(typeof(De4dotTools))]
    public sealed class De4dotTools
    {
        // ── Static list of all supported deobfuscators ───────────────────────

        static IList<IDeobfuscatorInfo> CreateDeobfuscatorInfos() =>
            new List<IDeobfuscatorInfo>
            {
                new de4dot.code.deobfuscators.Unknown.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Agile_NET.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Babel_NET.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.CodeFort.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.CodeVeil.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.CodeWall.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Confuser.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.CryptoObfuscator.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.DeepSea.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Dotfuscator.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.dotNET_Reactor.v3.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.dotNET_Reactor.v4.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Eazfuscator_NET.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Goliath_NET.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.ILProtector.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.MaxtoCode.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.MPRESS.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Obfuscar.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Rummage.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Skater_NET.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.SmartAssembly.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Spices_Net.DeobfuscatorInfo(),
                new de4dot.code.deobfuscators.Xenocode.DeobfuscatorInfo(),
            };

        static IList<IDeobfuscator> AllDeobfuscators() =>
            CreateDeobfuscatorInfos().Select(i => i.CreateDeobfuscator()).ToList();

        static IList<IDeobfuscator> DeobfuscatorsForMethod(string? method) =>
            CreateDeobfuscatorInfos()
                .Select(i => i.CreateDeobfuscator())
                .Where(d => method == null ||
                            d.TypeLong == method ||
                            d.Name    == method ||
                            d.Type    == method)
                .ToList();

        // ── Tool: list_deobfuscators ─────────────────────────────────────────

        /// <summary>List all deobfuscators supported by de4dot.</summary>
        public CallToolResult ListDeobfuscators(Dictionary<string, object>? _)
        {
            var infos = CreateDeobfuscatorInfos().Select(i => new
            {
                Type     = i.CreateDeobfuscator().Type,
                Name     = i.CreateDeobfuscator().Name,
                TypeLong = i.CreateDeobfuscator().TypeLong,
            }).ToList();

            var result = JsonSerializer.Serialize(new
            {
                Count         = infos.Count,
                Deobfuscators = infos
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        // ── Tool: detect_obfuscator ──────────────────────────────────────────

        /// <summary>
        /// Detect which obfuscator was applied to a .NET assembly file.
        /// Parameters:
        ///   file_path (required) — absolute path to the target DLL or EXE.
        /// </summary>
        public CallToolResult DetectObfuscator(Dictionary<string, object>? arguments)
        {
            var filePath = ResolveFilePath(arguments, "file_path");

            var moduleContext = new ModuleContext(TheAssemblyResolver.Instance);
            var options = new ObfuscatedFile.Options
            {
                Filename               = filePath,
                NewFilename            = Path.ChangeExtension(filePath, ".deobf.dll"),
                ControlFlowDeobfuscation = false,
                KeepObfuscatorTypes    = true,
                StringDecrypterType    = DecrypterType.None,
            };

            var assemblyClientFactory = new NewAppDomainAssemblyClientFactory();
            var file = new ObfuscatedFile(options, moduleContext, assemblyClientFactory);
            file.Load(AllDeobfuscators());

            var deob = file.Deobfuscator;

            // Clean up
            try { TheAssemblyResolver.Instance.Remove(file.ModuleDefMD); } catch { }
            file.Dispose();

            var result = JsonSerializer.Serialize(new
            {
                FilePath        = filePath,
                DetectedType    = deob.Type,
                DetectedName    = deob.Name,
                TypeLong        = deob.TypeLong,
                IsUnknown       = deob.Type == "un",
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        // ── Tool: deobfuscate_assembly ───────────────────────────────────────

        /// <summary>
        /// Deobfuscate a .NET assembly using de4dot.
        /// Parameters:
        ///   file_path             (required) — path to the obfuscated file.
        ///   output_path           (optional) — path for the cleaned output (default: &lt;name&gt;-cleaned.dll next to input).
        ///   method                (optional) — force a specific deobfuscator (Type/Name/TypeLong).
        ///   rename_symbols        (optional, default true) — rename obfuscated symbols.
        ///   control_flow          (optional, default true) — deobfuscate control flow.
        ///   keep_obfuscator_types (optional, default false) — keep obfuscator-internal types.
        ///   string_decrypter      (optional) — "none"|"static"|"delegate"|"emulate" (default "static").
        /// </summary>
        public CallToolResult DeobfuscateAssembly(Dictionary<string, object>? arguments)
        {
            var filePath = ResolveFilePath(arguments, "file_path");

            // Output path
            string outputPath;
            if (arguments != null && arguments.TryGetValue("output_path", out var outObj) &&
                !string.IsNullOrEmpty(outObj?.ToString()))
            {
                outputPath = outObj!.ToString()!;
            }
            else
            {
                var dir  = Path.GetDirectoryName(filePath)!;
                var stem = Path.GetFileNameWithoutExtension(filePath);
                var ext  = Path.GetExtension(filePath);
                outputPath = Path.Combine(dir, stem + "-cleaned" + ext);
            }

            // Options
            string? method = null;
            if (arguments != null && arguments.TryGetValue("method", out var mObj))
                method = mObj?.ToString();

            bool renameSymbols = true;
            if (arguments != null && arguments.TryGetValue("rename_symbols", out var rsObj))
                bool.TryParse(rsObj?.ToString(), out renameSymbols);

            bool controlFlow = true;
            if (arguments != null && arguments.TryGetValue("control_flow", out var cfObj))
                bool.TryParse(cfObj?.ToString(), out controlFlow);

            bool keepTypes = false;
            if (arguments != null && arguments.TryGetValue("keep_obfuscator_types", out var ktObj))
                bool.TryParse(ktObj?.ToString(), out keepTypes);

            var decrypterType = DecrypterType.Static;
            if (arguments != null && arguments.TryGetValue("string_decrypter", out var sdObj))
            {
                decrypterType = (sdObj?.ToString()?.ToLowerInvariant()) switch
                {
                    "none"     => DecrypterType.None,
                    "delegate" => DecrypterType.Delegate,
                    "emulate"  => DecrypterType.Emulate,
                    _          => DecrypterType.Static,
                };
            }

            int timeoutSeconds = 120;
            if (arguments != null && arguments.TryGetValue("timeout_seconds", out var tso))
            {
                if (tso is JsonElement je && je.TryGetInt32(out var ti)) timeoutSeconds = Math.Max(10, ti);
                else if (int.TryParse(tso?.ToString(), out var ti2)) timeoutSeconds = Math.Max(10, ti2);
            }

            // Capture de4dot log output via Console.Out redirect
            var logSb = new StringBuilder();
            var oldOut = Console.Out;
            Console.SetOut(new StringWriter(logSb));

            try
            {
                var renamerFlags = renameSymbols
                    ? (RenamerFlags.RenameNamespaces | RenamerFlags.RenameTypes |
                       RenamerFlags.RenameProperties | RenamerFlags.RenameEvents |
                       RenamerFlags.RenameFields     | RenamerFlags.RenameMethods |
                       RenamerFlags.RenameMethodArgs | RenamerFlags.RenameGenericParams |
                       RenamerFlags.RestoreProperties | RenamerFlags.RestoreEvents)
                    : 0;

                var moduleContext = new ModuleContext(TheAssemblyResolver.Instance);
                var fileOptions = new ObfuscatedFile.Options
                {
                    Filename                 = filePath,
                    NewFilename              = outputPath,
                    ControlFlowDeobfuscation = controlFlow,
                    KeepObfuscatorTypes      = keepTypes,
                    StringDecrypterType      = decrypterType,
                    RenamerFlags             = renamerFlags,
                };

                var assemblyClientFactory = new NewAppDomainAssemblyClientFactory();
                var obfFile = new ObfuscatedFile(fileOptions, moduleContext, assemblyClientFactory);
                obfFile.Load(DeobfuscatorsForMethod(method));

                var deob = obfFile.Deobfuscator;
                string detectedName = deob.TypeLong;

                using var deobfCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var deobfTask = Task.Run(() =>
                {
                    obfFile.DeobfuscateBegin();
                    obfFile.Deobfuscate();
                    obfFile.DeobfuscateEnd();
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    obfFile.Save();
                }, deobfCts.Token);

                if (!deobfTask.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    deobfCts.Cancel();
                    throw new TimeoutException($"deobfuscate_assembly timed out after {timeoutSeconds}s");
                }
                // Re-throw any exception from the task
                deobfTask.GetAwaiter().GetResult();

                try { TheAssemblyResolver.Instance.Remove(obfFile.ModuleDefMD); } catch { }
                obfFile.Dispose();

                long outputSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : -1;

                var result = JsonSerializer.Serialize(new
                {
                    InputPath      = filePath,
                    OutputPath     = outputPath,
                    DetectedMethod = detectedName,
                    OutputSizeBytes = outputSize,
                    RenameSymbols  = renameSymbols,
                    ControlFlow    = controlFlow,
                    StringDecrypter = decrypterType.ToString(),
                    Log            = logSb.Length > 0 ? logSb.ToString() : null,
                }, new JsonSerializerOptions { WriteIndented = true });

                return new CallToolResult
                {
                    Content = new List<ToolContent> { new ToolContent { Text = result } }
                };
            }
            finally
            {
                Console.SetOut(oldOut);
            }
        }

        // ── Tool: save_deobfuscated ──────────────────────────────────────────

        /// <summary>
        /// Return the deobfuscated file as a Base64 blob.
        /// Parameters:
        ///   file_path (required) — path to the already-deobfuscated file.
        ///   max_size_mb (optional, default 50) — refuse files larger than this.
        /// </summary>
        public CallToolResult SaveDeobfuscated(Dictionary<string, object>? arguments)
        {
            var filePath = ResolveFilePath(arguments, "file_path");

            int maxMb = 50;
            if (arguments != null && arguments.TryGetValue("max_size_mb", out var maxObj) &&
                int.TryParse(maxObj?.ToString(), out var m) && m > 0)
                maxMb = m;

            var info = new FileInfo(filePath);
            if (info.Length > maxMb * 1024L * 1024L)
                throw new ArgumentException(
                    $"File too large ({info.Length / (1024 * 1024)} MB > {maxMb} MB limit). Increase max_size_mb or use the output_path from deobfuscate_assembly.");

            var bytes  = File.ReadAllBytes(filePath);
            var base64 = Convert.ToBase64String(bytes);

            var result = JsonSerializer.Serialize(new
            {
                FilePath   = filePath,
                SizeBytes  = info.Length,
                Base64     = base64,
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        static string ResolveFilePath(Dictionary<string, object>? args, string key)
        {
            if (args == null || !args.TryGetValue(key, out var obj) ||
                string.IsNullOrEmpty(obj?.ToString()))
                throw new ArgumentException($"{key} is required");

            var path = obj!.ToString()!;
            if (!File.Exists(path))
                throw new ArgumentException($"File not found: {path}");
            return path;
        }

    }
}

// ── run_de4dot ────────────────────────────────────────────────────────────────

namespace dnSpy.MCP.Server.Application
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.Composition;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using dnSpy.MCP.Server.Configuration;
    using dnSpy.MCP.Server.Contracts;

    /// <summary>
    /// Invokes de4dot.exe as an external process so that all de4dot features
    /// (including dynamic assembly-server decryption) are available from the MCP
    /// without requiring the net48 in-process libraries.
    /// Works in both net10 and net48 builds.
    /// Path resolution order: argument override → mcp-config.json → sibling repo heuristic.
    /// </summary>
    [Export(typeof(De4dotExeTool))]
    public sealed class De4dotExeTool
    {
        static string? FindDe4dotExe(string? explicitPath)
        {
            // Argument-level override always wins
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
                return explicitPath!;

            // Delegate all other resolution to McpConfig
            return McpConfig.Instance.ResolveDe4dotExe();
        }

        /// <summary>
        /// Run de4dot.exe with configurable arguments.
        /// Arguments:
        ///   file_path*       – input assembly
        ///   output_path      – output file (default: file_path + ".deobfuscated.exe")
        ///   obfuscator_type  – de4dot type code: cr, un, an, bl, co, ... (default: auto)
        ///   dont_rename      – bool, suppress symbol renaming (default false)
        ///   no_cflow_deob    – bool, skip control-flow deobfuscation (default false)
        ///   string_decrypter – none|default|static|delegate|emulate (default: default)
        ///   extra_args       – free-form extra de4dot arguments string
        ///   de4dot_path      – override path to de4dot.exe
        ///   timeout_ms       – max time to wait (default 120000)
        /// </summary>
        public CallToolResult RunDe4dot(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("file_path", out var fpObj) || fpObj?.ToString() is not string filePath || !File.Exists(filePath))
                throw new ArgumentException("file_path is required and must point to an existing file");

            string? explicitDe4dot = null;
            if (arguments.TryGetValue("de4dot_path", out var dp)) explicitDe4dot = dp?.ToString();
            var de4dotExe = FindDe4dotExe(explicitDe4dot)
                ?? throw new InvalidOperationException(
                    "de4dot.exe not found. Set 'de4dot_path' argument or place de4dot.exe next to the MCP server DLL.");

            string outputPath = filePath + ".deobfuscated.exe";
            if (arguments.TryGetValue("output_path", out var op) && !string.IsNullOrWhiteSpace(op?.ToString()))
                outputPath = op!.ToString()!;

            var outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            // Build argument list
            var sb = new StringBuilder();
            sb.Append($"-f \"{filePath}\" -o \"{outputPath}\"");

            if (arguments.TryGetValue("obfuscator_type", out var ot) && !string.IsNullOrWhiteSpace(ot?.ToString()))
                sb.Append($" -p {ot}");

            if (arguments.TryGetValue("dont_rename", out var dr) && dr?.ToString()?.ToLowerInvariant() == "true")
                sb.Append(" --dont-rename");

            if (arguments.TryGetValue("no_cflow_deob", out var cf) && cf?.ToString()?.ToLowerInvariant() == "true")
                sb.Append(" --no-cflow-deob");

            if (arguments.TryGetValue("string_decrypter", out var sd) && !string.IsNullOrWhiteSpace(sd?.ToString()))
                sb.Append($" --default-strtyp {sd}");

            if (arguments.TryGetValue("extra_args", out var ea) && !string.IsNullOrWhiteSpace(ea?.ToString()))
                sb.Append($" {ea}");

            int timeoutMs = 120000;
            if (arguments.TryGetValue("timeout_ms", out var to) && to is JsonElement toElem && toElem.TryGetInt32(out var toInt))
                timeoutMs = Math.Max(5000, toInt);

            // Run de4dot.exe
            var psi = new ProcessStartInfo(de4dotExe, sb.ToString())
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                WorkingDirectory       = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory()
            };

            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuf.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            bool exited = proc.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                throw new TimeoutException($"de4dot timed out after {timeoutMs}ms");
            }

            bool success = proc.ExitCode == 0 && File.Exists(outputPath);
            var result = JsonSerializer.Serialize(new
            {
                Success    = success,
                ExitCode   = proc.ExitCode,
                InputPath  = filePath,
                OutputPath = success ? outputPath : (string?)null,
                SizeBytes  = success ? (long?)new FileInfo(outputPath).Length : null,
                Arguments  = sb.ToString(),
                De4dotExe  = de4dotExe,
                Stdout     = stdoutBuf.ToString().Trim(),
                Stderr     = stderrBuf.ToString().Trim()
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            return new CallToolResult
            {
                Content  = new List<ToolContent> { new ToolContent { Text = result } },
                IsError  = !success
            };
        }
    }
}
