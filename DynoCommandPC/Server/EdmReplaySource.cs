using System.Globalization;
using System.IO;
using System.Linq;
using DynoCommandPC.Protocol;

namespace DynoCommandPC.Server;

/// <summary>
/// Loads a captured dyno log and extracts the <b>dyno-&gt;handheld</b> (RX-direction)
/// frames with their original inter-frame timing, for replay by the EDM server.
/// Accepts the dyno-capture text format: "HH:MM:SS.mmm  DIR  B0 B1 .. B9" — only
/// the RX lines (what the EDM transmitted) are used. CRC is recomputed at send.
/// </summary>
public static class EdmReplaySource
{
    public sealed record Frame(CanPacket Packet, int DelayMs);

    public static IReadOnlyList<Frame> Load(string path)
    {
        var frames = new List<Frame>();
        double? prevSec = null;

        foreach (var raw in File.ReadLines(path))
        {
            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            // Find the TX/RX direction token; keep only RX (EDM -> handheld).
            int dir = Array.FindIndex(parts, t => t is "RX" or "TX");
            if (dir < 0 || parts[dir] != "RX") continue;

            var hex = parts.Skip(dir + 1).ToArray();
            if (hex.Length < 8) continue;

            var b = new byte[10];
            bool ok = true;
            for (int i = 0; i < 10; i++)
            {
                if (i < hex.Length &&
                    byte.TryParse(hex[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i]))
                    continue;
                if (i >= 8) { b[i] = 0; continue; } // CRC bytes optional (recomputed)
                ok = false; break;
            }
            if (!ok) continue;

            double sec = TryParseTime(parts[0], out double s) ? s : (prevSec ?? 0);
            int delay = prevSec.HasValue ? (int)Math.Clamp((sec - prevSec.Value) * 1000.0, 0, 1000) : 0;
            frames.Add(new Frame(CanPacket.FromBytes(b), delay));
            prevSec = sec;
        }
        return frames;
    }

    private static bool TryParseTime(string s, out double seconds)
    {
        seconds = 0;
        var hms = s.Split(':');
        if (hms.Length != 3) return false;
        if (!int.TryParse(hms[0], out int h)) return false;
        if (!int.TryParse(hms[1], out int m)) return false;
        if (!double.TryParse(hms[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) &&
            !double.TryParse(hms[2], out sec)) return false;
        seconds = h * 3600 + m * 60 + sec;
        return true;
    }
}
