using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RocketWorld.Net
{
    /// <summary>Keeps the last warnings/errors/exceptions (and our own [RocketWorld]/[Multiplayer] lines) so the UI can copy them in one tap.</summary>
    public static class NetLog
    {
        const int Max = 80;
        static readonly List<string> _lines = new List<string>();
        static readonly object _lock = new object();
        static string _last; static int _repeat;
        static bool _hooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            bool own = msg.StartsWith("[RocketWorld]", StringComparison.Ordinal) || msg.StartsWith("[Multiplayer]", StringComparison.Ordinal);
            if (type == LogType.Log && !own) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("HH:mm:ss")).Append(' ').Append(type == LogType.Log ? "INFO" : type.ToString().ToUpperInvariant()).Append(' ').Append(msg);
            if ((type == LogType.Exception || type == LogType.Error) && !string.IsNullOrEmpty(stack))
            {
                string s = stack.Trim();
                sb.Append('\n').Append(s.Length > 700 ? s.Substring(0, 700) + " …" : s);
            }
            string line = sb.ToString();
            string key = msg;
            lock (_lock)
            {
                if (key == _last && _lines.Count > 0) { _repeat++; _lines[_lines.Count - 1] = line + "  (x" + (_repeat + 1) + ")"; return; }
                _last = key; _repeat = 0;
                _lines.Add(line);
                if (_lines.Count > Max) _lines.RemoveAt(0);
            }
        }

        public static string Dump(string header)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== RocketWorld · errores ===");
            if (!string.IsNullOrEmpty(header)) sb.AppendLine(header);
            sb.AppendLine("Unity " + Application.unityVersion + " · " + Application.platform + " · v" + Application.version);
            sb.AppendLine("cloudProjectId: " + Application.cloudProjectId);
            sb.AppendLine("---");
            lock (_lock)
            {
                if (_lines.Count == 0) sb.AppendLine("(sin errores registrados)");
                foreach (var l in _lines) sb.AppendLine(l);
            }
            return sb.ToString();
        }
    }
}
