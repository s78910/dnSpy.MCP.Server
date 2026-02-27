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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using WpfApp = System.Windows.Application;

namespace dnSpy.MCP.Server.Application
{
    [Export(typeof(WindowTools))]
    public sealed class WindowTools
    {
        // ── P/Invoke ──────────────────────────────────────────────────────────

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc fn, IntPtr lp);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        const uint WM_CLOSE = 0x0010;
        const uint BM_CLICK = 0x00F5;

        // ── Constructor ───────────────────────────────────────────────────────

        [ImportingConstructor]
        public WindowTools() { }

        // ── Internal helpers ──────────────────────────────────────────────────

        static string GetWndText(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static string GetWndClass(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        // Collect all child buttons of a Win32 dialog
        static List<(IntPtr hwnd, string text)> GetButtons(IntPtr parent)
        {
            var buttons = new List<(IntPtr, string)>();
            EnumChildWindows(parent, (child, _) =>
            {
                if (GetWndClass(child).Equals("Button", StringComparison.OrdinalIgnoreCase))
                    buttons.Add((child, GetWndText(child).Trim()));
                return true;
            }, IntPtr.Zero);
            return buttons;
        }

        // Collect "Static" child texts (message body) of a Win32 dialog
        static string GetDialogMessage(IntPtr parent)
        {
            var parts = new List<string>();
            EnumChildWindows(parent, (child, _) =>
            {
                if (GetWndClass(child).Equals("Static", StringComparison.OrdinalIgnoreCase))
                {
                    string t = GetWndText(child).Trim();
                    if (t.Length > 0)
                        parts.Add(t);
                }
                return true;
            }, IntPtr.Zero);
            return string.Join(" | ", parts);
        }

        // ── Dialog descriptor ─────────────────────────────────────────────────

        sealed class DialogInfo
        {
            public IntPtr Hwnd;          // IntPtr.Zero for pure-WPF dialogs
            public string Title = "";
            public string Message = "";
            public List<string> Buttons = new List<string>();
            public bool IsWpf;
            public Window? WpfWindow;    // only for WPF dialogs
        }

        List<DialogInfo> CollectDialogs()
        {
            var result = new List<DialogInfo>();
            uint currentPid = (uint)Process.GetCurrentProcess().Id;

            // A) WPF windows (non-main windows visible on screen)
            WpfApp.Current.Dispatcher.Invoke(() =>
            {
                Window? mainWin = WpfApp.Current.MainWindow;
                foreach (Window w in WpfApp.Current.Windows)
                {
                    if (!w.IsVisible || w == mainWin)
                        continue;

                    var di = new DialogInfo
                    {
                        IsWpf = true,
                        WpfWindow = w,
                        Title = w.Title ?? "",
                        // WPF MessageBox wraps a Win32 dialog; collect buttons below.
                        Message = "",
                        Buttons = new List<string>()
                    };

                    // Try to get underlying HWND (may or may not exist for WPF MessageBox)
                    try
                    {
                        var interop = new System.Windows.Interop.WindowInteropHelper(w);
                        IntPtr h = interop.Handle;
                        if (h != IntPtr.Zero)
                        {
                            di.Hwnd = h;
                            di.Message = GetDialogMessage(h);
                            foreach (var (_, txt) in GetButtons(h))
                                if (txt.Length > 0) di.Buttons.Add(txt);
                        }
                    }
                    catch { /* ignore */ }

                    result.Add(di);
                }
            });

            // B) Win32 dialogs (class #32770) owned by this process
            var seenHwnds = new HashSet<IntPtr>();
            foreach (var d in result)
                if (d.Hwnd != IntPtr.Zero) seenHwnds.Add(d.Hwnd);

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != currentPid) return true;
                if (!GetWndClass(hwnd).Equals("#32770", StringComparison.Ordinal)) return true;
                if (seenHwnds.Contains(hwnd)) return true;

                var di = new DialogInfo
                {
                    IsWpf = false,
                    Hwnd = hwnd,
                    Title = GetWndText(hwnd),
                    Message = GetDialogMessage(hwnd)
                };
                foreach (var (_, txt) in GetButtons(hwnd))
                    if (txt.Length > 0) di.Buttons.Add(txt);

                result.Add(di);
                return true;
            }, IntPtr.Zero);

            return result;
        }

        // ── Public tools ──────────────────────────────────────────────────────

        public string ListDialogs(Dictionary<string, object>? args = null)
        {
            var dialogs = CollectDialogs();
            if (dialogs.Count == 0)
                return "No hay diálogos activos.";

            var sb = new StringBuilder();
            for (int i = 0; i < dialogs.Count; i++)
            {
                var d = dialogs[i];
                sb.AppendLine($"[{i + 1}] Title: \"{d.Title}\"");
                if (d.Hwnd != IntPtr.Zero)
                    sb.AppendLine($"    Hwnd: {d.Hwnd.ToInt64():X}  |  Type: {(d.IsWpf ? "WPF" : "Win32 (#32770)")}");
                else
                    sb.AppendLine($"    Type: WPF (no HWND)");
                if (!string.IsNullOrWhiteSpace(d.Message))
                    sb.AppendLine($"    Message: \"{d.Message}\"");
                if (d.Buttons.Count > 0)
                    sb.AppendLine($"    Buttons: {string.Join(", ", d.Buttons)}");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        public string CloseDialog(Dictionary<string, object>? args)
        {
            args ??= new Dictionary<string, object>();

            // Parse optional hwnd
            IntPtr targetHwnd = IntPtr.Zero;
            if (args.TryGetValue("hwnd", out object? hwndObj) && hwndObj is not null)
            {
                string hwndStr = hwndObj is JsonElement je ? je.GetString() ?? "" : hwndObj.ToString() ?? "";
                hwndStr = hwndStr.Trim();
                if (hwndStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hwndStr = hwndStr.Substring(2);
                if (long.TryParse(hwndStr, System.Globalization.NumberStyles.HexNumber, null, out long hv))
                    targetHwnd = new IntPtr(hv);
            }

            // Parse optional button preference
            string buttonPref = "ok";
            if (args.TryGetValue("button", out object? btnObj) && btnObj is not null)
            {
                string raw = btnObj is JsonElement je2 ? je2.GetString() ?? "" : btnObj.ToString() ?? "";
                if (raw.Length > 0)
                    buttonPref = raw.Trim().ToLowerInvariant();
            }

            var dialogs = CollectDialogs();
            if (dialogs.Count == 0)
                return "No hay diálogos activos.";

            // Resolve target dialog
            DialogInfo? target = null;
            if (targetHwnd != IntPtr.Zero)
            {
                foreach (var d in dialogs)
                    if (d.Hwnd == targetHwnd) { target = d; break; }
                if (target == null)
                    return $"Error: no se encontró un diálogo con HWND {targetHwnd.ToInt64():X}.";
            }
            else
            {
                target = dialogs[0];
            }

            // Click matching button on Win32 dialog
            if (target.Hwnd != IntPtr.Zero)
            {
                var buttons = GetButtons(target.Hwnd);
                IntPtr matchedBtn = IntPtr.Zero;
                string matchedText = "";

                foreach (var (btnHwnd, btnText) in buttons)
                {
                    if (ButtonMatches(buttonPref, btnText))
                    {
                        matchedBtn = btnHwnd;
                        matchedText = btnText;
                        break;
                    }
                }

                if (matchedBtn != IntPtr.Zero)
                {
                    SendMessage(matchedBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    return $"Clicked '{matchedText}' in dialog '{target.Title}'.";
                }

                // Fallback: close the window
                PostMessage(target.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return $"No matching button for '{buttonPref}' found; sent WM_CLOSE to dialog '{target.Title}'.";
            }

            // Pure WPF window without HWND — close via Dispatcher
            if (target.WpfWindow != null)
            {
                WpfApp.Current.Dispatcher.Invoke(() => target.WpfWindow.Close());
                return $"Closed WPF dialog '{target.Title}'.";
            }

            return "Error: el diálogo no tiene HWND ni referencia WPF.";
        }

        // ── Button matching ───────────────────────────────────────────────────

        static bool ButtonMatches(string pref, string btnText)
        {
            string lower = btnText.ToLowerInvariant();
            switch (pref)
            {
                case "ok":
                case "accept":
                case "aceptar":
                    return lower == "ok" || lower == "aceptar" || lower == "accept";

                case "yes":
                case "sí":
                case "si":
                    return lower == "yes" || lower == "sí" || lower == "si";

                case "no":
                    return lower == "no";

                case "cancel":
                case "cancelar":
                    return lower == "cancel" || lower == "cancelar";

                case "retry":
                case "reintentar":
                    return lower == "retry" || lower == "reintentar";

                case "ignore":
                case "omitir":
                    return lower == "ignore" || lower == "omitir";

                default:
                    return lower.Contains(pref);
            }
        }
    }
}
