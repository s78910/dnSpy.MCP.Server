// De4dotTools.cs — de4dot deobfuscation integration for dnSpy MCP Server
// Mirrors the tools in de4dot.mcp (detect_obfuscator, deobfuscate_assembly,
// list_deobfuscators, save_deobfuscated) but runs in-process inside dnSpy.
#if NETFRAMEWORK   // de4dot libraries are only available in the net48 build
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

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
                outputPath = outObj.ToString()!;
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

                obfFile.DeobfuscateBegin();
                obfFile.Deobfuscate();
                obfFile.DeobfuscateEnd();

                // Save output
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                obfFile.Save();

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

            var path = obj.ToString()!;
            if (!File.Exists(path))
                throw new ArgumentException($"File not found: {path}");
            return path;
        }

    }
}
#endif
