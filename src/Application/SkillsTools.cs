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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// Skills knowledge base: persistent storage of reverse-engineering procedures, findings,
	/// magic values, crypto keys, prompts, and step-by-step workflows per binary/protection.
	/// Each skill is a Markdown narrative + JSON technical record stored in
	/// %APPDATA%\dnSpy\dnSpy.MCPServer\skills\
	/// </summary>
	[Export(typeof(SkillsTools))]
	public sealed class SkillsTools {
		static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions {
			WriteIndented = true,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		// ── Storage path ─────────────────────────────────────────────────────────

		static string SkillsDir {
			get {
				var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				return Path.Combine(appData, "dnSpy", "dnSpy.MCPServer", "skills");
			}
		}

		static string MdPath(string id)   => Path.Combine(SkillsDir, id + ".md");
		static string JsonPath(string id) => Path.Combine(SkillsDir, id + ".json");

		static void EnsureDir() => Directory.CreateDirectory(SkillsDir);

		// Slugify: keep lowercase alphanumeric and hyphens, collapse others to '-'
		static string Slugify(string s) {
			s = s.ToLowerInvariant().Trim();
			s = Regex.Replace(s, @"[^a-z0-9]+", "-");
			s = s.Trim('-');
			if (string.IsNullOrEmpty(s))
				throw new ArgumentException("skill_id results in empty slug after sanitization");
			return s;
		}

		static bool GetBool(Dictionary<string, object> args, string key, bool def) {
			if (!args.TryGetValue(key, out var v)) return def;
			if (v is bool b) return b;
			if (v is JsonElement je)
				return je.ValueKind == JsonValueKind.True;
			return def;
		}

		static string? GetStr(Dictionary<string, object> args, string key) {
			if (!args.TryGetValue(key, out var v)) return null;
			if (v is JsonElement je && je.ValueKind == JsonValueKind.String)
				return je.GetString();
			return v?.ToString();
		}

		// ── Skill index helpers ───────────────────────────────────────────────────

		sealed class SkillSummary {
			public string Id;
			public string Name;
			public string Description;
			public string[] Tags;
			public string[] Targets;
			public string Updated;
			public string MdFile;
			public string JsonFile;
			public SkillSummary(string id, string name, string description, string[] tags, string[] targets, string updated, string mdFile, string jsonFile) {
				Id = id; Name = name; Description = description; Tags = tags; Targets = targets; Updated = updated; MdFile = mdFile; JsonFile = jsonFile;
			}
		}

		static List<SkillSummary> LoadAllSummaries() {
			EnsureDir();
			var summaries = new List<SkillSummary>();
			foreach (var jsonFile in Directory.GetFiles(SkillsDir, "*.json")) {
				try {
					var id = Path.GetFileNameWithoutExtension(jsonFile);
					var raw = File.ReadAllText(jsonFile, Encoding.UTF8);
					using var doc = JsonDocument.Parse(raw);
					var root = doc.RootElement;
					summaries.Add(new SkillSummary(
						id:          id,
						name:        root.TryGetProperty("name",        out var n) ? n.GetString() ?? id : id,
						description: root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
						tags:        root.TryGetProperty("tags",        out var t) ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
						targets:     root.TryGetProperty("targets",     out var tg) ? tg.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
						updated:     root.TryGetProperty("updated",     out var u) ? u.GetString() ?? "" : "",
						mdFile:      Path.ChangeExtension(jsonFile, ".md"),
						jsonFile:    jsonFile
					));
				}
				catch (Exception ex) {
					McpLogger.Warning($"SkillsTools: failed to parse {jsonFile}: {ex.Message}");
				}
			}
			return summaries;
		}

		// ── list_skills ───────────────────────────────────────────────────────────

		/// <summary>
		/// List all skills in the knowledge base.
		/// Arguments: tag (opt string filter)
		/// </summary>
		public CallToolResult ListSkills(Dictionary<string, object>? arguments) {
			var tagFilter = arguments != null ? GetStr(arguments, "tag") : null;
			var summaries = LoadAllSummaries();

			if (!string.IsNullOrEmpty(tagFilter)) {
				var tf = tagFilter!.ToLowerInvariant();
				summaries = summaries
					.Where(s => s.Tags.Any(t => t.ToLowerInvariant().Contains(tf)))
					.ToList();
			}

			var items = summaries.OrderBy(s => s.Id).Select(s => new {
				s.Id,
				s.Name,
				s.Description,
				s.Tags,
				s.Targets,
				s.Updated,
				HasMarkdown = File.Exists(s.MdFile),
				HasJson     = File.Exists(s.JsonFile)
			}).ToList();

			var result = JsonSerializer.Serialize(new {
				SkillsDirectory = SkillsDir,
				Count           = items.Count,
				Skills          = items
			}, JsonOpts);

			return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = result } } };
		}

		// ── get_skill ─────────────────────────────────────────────────────────────

		/// <summary>
		/// Retrieve the full content of a skill: Markdown narrative + JSON technical record.
		/// Arguments: skill_id
		/// </summary>
		public CallToolResult GetSkill(Dictionary<string, object>? arguments) {
			if (arguments == null) throw new ArgumentException("Arguments required");
			var rawId = GetStr(arguments, "skill_id")
				?? throw new ArgumentException("skill_id is required");
			var id = Slugify(rawId);

			var mdFile   = MdPath(id);
			var jsonFile = JsonPath(id);

			if (!File.Exists(jsonFile) && !File.Exists(mdFile))
				throw new ArgumentException($"Skill not found: '{id}'. Use list_skills to see available skills.");

			string? markdown = File.Exists(mdFile)   ? File.ReadAllText(mdFile,   Encoding.UTF8) : null;
			string? jsonRaw  = File.Exists(jsonFile) ? File.ReadAllText(jsonFile, Encoding.UTF8) : null;

			// Parse JSON to surface key fields in the response envelope
			string name = id, description = "", updated = "";
			string[] tags = Array.Empty<string>(), targets = Array.Empty<string>();
			if (jsonRaw != null) {
				try {
					using var doc = JsonDocument.Parse(jsonRaw);
					var root = doc.RootElement;
					if (root.TryGetProperty("name",        out var n))  name        = n.GetString() ?? id;
					if (root.TryGetProperty("description", out var de)) description = de.GetString() ?? "";
					if (root.TryGetProperty("updated",     out var u))  updated     = u.GetString() ?? "";
					if (root.TryGetProperty("tags",    out var t))  tags    = t.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
					if (root.TryGetProperty("targets", out var tg)) targets = tg.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
				}
				catch { /* return raw even if malformed */ }
			}

			var result = JsonSerializer.Serialize(new {
				Id          = id,
				Name        = name,
				Description = description,
				Tags        = tags,
				Targets     = targets,
				Updated     = updated,
				MdPath      = mdFile,
				JsonPath    = jsonFile,
				Markdown    = markdown,
				Json        = jsonRaw
			}, JsonOpts);

			return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = result } } };
		}

		// ── save_skill ────────────────────────────────────────────────────────────

		/// <summary>
		/// Create or update a skill. Writes a Markdown file and a JSON technical record.
		/// If merge=true, deep-merges JSON into existing record instead of replacing.
		/// Arguments: skill_id*, name, description, tags, targets, markdown, json_data, merge
		/// </summary>
		public CallToolResult SaveSkill(Dictionary<string, object>? arguments) {
			if (arguments == null) throw new ArgumentException("Arguments required");

			var rawId = GetStr(arguments, "skill_id")
				?? throw new ArgumentException("skill_id is required");
			var id = Slugify(rawId);

			EnsureDir();
			var mdFile   = MdPath(id);
			var jsonFile = JsonPath(id);
			var now      = DateTime.UtcNow.ToString("o");
			bool merge   = GetBool(arguments, "merge", false);

			// ── Markdown ─────────────────────────────────────────────
			var newMarkdown = GetStr(arguments, "markdown");
			if (newMarkdown != null)
				File.WriteAllText(mdFile, newMarkdown, Encoding.UTF8);

			// ── JSON record ──────────────────────────────────────────
			var newJsonRaw = GetStr(arguments, "json_data");

			Dictionary<string, object?> merged;
			if (merge && File.Exists(jsonFile)) {
				// Load existing, then overlay new values
				merged = LoadJsonDict(File.ReadAllText(jsonFile, Encoding.UTF8));
				if (newJsonRaw != null) {
					var newDict = LoadJsonDict(newJsonRaw);
					foreach (var kv in newDict) merged[kv.Key] = kv.Value;
				}
			}
			else if (newJsonRaw != null) {
				merged = LoadJsonDict(newJsonRaw);
			}
			else if (File.Exists(jsonFile)) {
				merged = LoadJsonDict(File.ReadAllText(jsonFile, Encoding.UTF8));
			}
			else {
				merged = new Dictionary<string, object?>();
			}

			// Overlay envelope fields from explicit arguments
			merged["id"]      = id;
			merged["updated"] = now;
			if (!merged.ContainsKey("created")) merged["created"] = now;

			OverlayIfPresent(arguments, merged, "name");
			OverlayIfPresent(arguments, merged, "description");

			// tags / targets: accept comma-separated string or JSON array string
			OverlayList(arguments, merged, "tags");
			OverlayList(arguments, merged, "targets");

			// Write JSON
			var jsonOut = JsonSerializer.Serialize(merged, JsonOpts);
			File.WriteAllText(jsonFile, jsonOut, Encoding.UTF8);

			McpLogger.Info($"SkillsTools: saved skill '{id}'");

			var result = JsonSerializer.Serialize(new {
				Saved    = id,
				MdPath   = mdFile,
				JsonPath = jsonFile,
				Merged   = merge,
				Updated  = now
			}, JsonOpts);

			return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = result } } };
		}

		// ── search_skills ─────────────────────────────────────────────────────────

		/// <summary>
		/// Search skills by keyword (scans Markdown + JSON content) and/or tag.
		/// Arguments: query (opt), tag (opt)
		/// </summary>
		public CallToolResult SearchSkills(Dictionary<string, object>? arguments) {
			var query    = arguments != null ? GetStr(arguments, "query") : null;
			var tagFilter = arguments != null ? GetStr(arguments, "tag")  : null;

			if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(tagFilter))
				throw new ArgumentException("Provide at least one of: query, tag");

			var summaries = LoadAllSummaries();
			var results   = new List<object>();

			foreach (var s in summaries) {
				// Tag filter
				if (!string.IsNullOrEmpty(tagFilter)) {
					var tf = tagFilter!.ToLowerInvariant();
					if (!s.Tags.Any(t => t.ToLowerInvariant().Contains(tf)))
						continue;
				}

				string? snippet = null;
				if (!string.IsNullOrWhiteSpace(query)) {
					var q = query!.ToLowerInvariant();
					// Search markdown
					if (File.Exists(s.MdFile)) {
						var md = File.ReadAllText(s.MdFile, Encoding.UTF8);
						snippet = FindSnippet(md, q);
					}
					// Search JSON if not found in markdown
					if (snippet == null && File.Exists(s.JsonFile)) {
						var json = File.ReadAllText(s.JsonFile, Encoding.UTF8);
						snippet = FindSnippet(json, q);
					}
					if (snippet == null)
						continue; // query not found
				}

				results.Add(new {
					s.Id,
					s.Name,
					s.Description,
					s.Tags,
					s.Targets,
					s.Updated,
					Snippet = snippet
				});
			}

			var result = JsonSerializer.Serialize(new {
				Query   = query,
				Tag     = tagFilter,
				Count   = results.Count,
				Results = results
			}, JsonOpts);

			return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = result } } };
		}

		// ── delete_skill ──────────────────────────────────────────────────────────

		/// <summary>
		/// Delete a skill's Markdown and JSON files.
		/// Arguments: skill_id
		/// </summary>
		public CallToolResult DeleteSkill(Dictionary<string, object>? arguments) {
			if (arguments == null) throw new ArgumentException("Arguments required");
			var rawId = GetStr(arguments, "skill_id")
				?? throw new ArgumentException("skill_id is required");
			var id = Slugify(rawId);

			var mdFile   = MdPath(id);
			var jsonFile = JsonPath(id);

			if (!File.Exists(mdFile) && !File.Exists(jsonFile))
				throw new ArgumentException($"Skill not found: '{id}'. Use list_skills to see available skills.");

			bool deletedMd   = false;
			bool deletedJson = false;
			if (File.Exists(mdFile))   { File.Delete(mdFile);   deletedMd   = true; }
			if (File.Exists(jsonFile)) { File.Delete(jsonFile); deletedJson = true; }

			McpLogger.Info($"SkillsTools: deleted skill '{id}'");

			var result = JsonSerializer.Serialize(new {
				Deleted      = id,
				DeletedMd    = deletedMd,
				DeletedJson  = deletedJson
			}, JsonOpts);

			return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = result } } };
		}

		// ── Helpers ───────────────────────────────────────────────────────────────

		static Dictionary<string, object?> LoadJsonDict(string json) {
			try {
				var doc  = JsonDocument.Parse(json);
				return DeserializeDict(doc.RootElement);
			}
			catch {
				return new Dictionary<string, object?>();
			}
		}

		static Dictionary<string, object?> DeserializeDict(JsonElement el) {
			var d = new Dictionary<string, object?>();
			if (el.ValueKind != JsonValueKind.Object) return d;
			foreach (var prop in el.EnumerateObject())
				d[prop.Name] = DeserializeValue(prop.Value);
			return d;
		}

		static object? DeserializeValue(JsonElement el) => el.ValueKind switch {
			JsonValueKind.String  => el.GetString(),
			JsonValueKind.Number  => el.TryGetInt64(out var i) ? (object)i : el.GetDouble(),
			JsonValueKind.True    => true,
			JsonValueKind.False   => false,
			JsonValueKind.Null    => null,
			JsonValueKind.Object  => DeserializeDict(el),
			JsonValueKind.Array   => el.EnumerateArray().Select(DeserializeValue).ToList(),
			_                     => el.GetRawText()
		};

		static void OverlayIfPresent(Dictionary<string, object> args, Dictionary<string, object?> target, string key) {
			var v = GetStr(args, key);
			if (v != null) target[key] = v;
		}

		static void OverlayList(Dictionary<string, object> args, Dictionary<string, object?> target, string key) {
			if (!args.TryGetValue(key, out var raw)) return;
			var s = raw is JsonElement je ? je.GetRawText().Trim() : raw?.ToString()?.Trim() ?? "";
			if (s.StartsWith("[")) {
				// JSON array string
				try {
					using var doc = JsonDocument.Parse(s);
					target[key] = doc.RootElement.EnumerateArray().Select(x => (object?)(x.GetString() ?? "")).ToList();
					return;
				}
				catch { }
			}
			// Comma-separated
			target[key] = s.Split(',').Select(x => (object?)x.Trim()).Where(x => !string.IsNullOrEmpty((string?)x)).ToList();
		}

		static string? FindSnippet(string text, string query, int contextChars = 120) {
			var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
			if (idx < 0) return null;
			var start = Math.Max(0, idx - contextChars / 2);
			var end   = Math.Min(text.Length, idx + query.Length + contextChars / 2);
			var snippet = text.Substring(start, end - start).Replace('\n', ' ').Replace('\r', ' ');
			if (start > 0) snippet = "…" + snippet;
			if (end < text.Length) snippet += "…";
			return snippet;
		}
	}
}
