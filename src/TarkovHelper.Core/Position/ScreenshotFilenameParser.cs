using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovHelper.Core.Position;

// Parses player position/rotation out of the filename EFT writes when a
// screenshot is taken - not image metadata, not a companion file. Verified
// against a real filename sample and TarkovMonitor's GameWatcher.cs regexes:
//   2025-12-25[10-14]_-519.33, -39.61, 68.41_-0.04164, 0.80479, -0.05690, -0.58935_5.68 (0).png
public static class ScreenshotFilenameParser
{
    private static readonly Regex EnvelopePattern = new(
        @"\d{4}-\d{2}-\d{2}\[\d{2}-\d{2}\]_?(?<position>.+) \(\d\)\.png",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PositionPattern = new(
        @"(?<x>-?[\d]+\.[\d]{2}), (?<y>-?[\d]+\.[\d]{2}), (?<z>-?[\d]+\.[\d]{2})_?" +
        @"(?<rx>-?[\d.]{1}\.[\d]{1,5}), (?<ry>-?[\d.]{1}\.[\d]{1,5}), (?<rz>-?[\d.]{1}\.[\d]{1,5}), (?<rw>-?[\d.]{1}\.[\d]{1,5})",
        RegexOptions.Compiled);

    public static bool TryParse(string filename, out PlayerPosition position)
    {
        position = default;

        var envelopeMatch = EnvelopePattern.Match(filename);
        if (!envelopeMatch.Success)
        {
            return false;
        }

        var positionMatch = PositionPattern.Match(envelopeMatch.Groups["position"].Value);
        if (!positionMatch.Success)
        {
            return false;
        }

        var x = ParseFloat(positionMatch.Groups["x"].Value);
        var y = ParseFloat(positionMatch.Groups["y"].Value);
        var z = ParseFloat(positionMatch.Groups["z"].Value);
        var rx = ParseFloat(positionMatch.Groups["rx"].Value);
        var ry = ParseFloat(positionMatch.Groups["ry"].Value);
        var rz = ParseFloat(positionMatch.Groups["rz"].Value);
        var rw = ParseFloat(positionMatch.Groups["rw"].Value);

        var yaw = QuaternionToYaw(rx, ry, rz, rw);

        position = new PlayerPosition(x, y, z, yaw, DateTime.Now);
        return true;
    }

    private static float ParseFloat(string value) =>
        float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    // Verbatim from TarkovMonitor's QuarternionsToYaw: called positionally
    // as (rx, ry, rz, rw) but the method's OWN parameters are misleadingly
    // named (x, z, y, w) - i.e. the second positional argument (ry) binds
    // to the parameter named "z", and the third (rz) binds to "y". This is
    // not a call-site swap to replicate; the call site passes rx,ry,rz,rw
    // in that literal order, matching the regex group order exactly. An
    // earlier version of this code incorrectly "fixed" what looked like a
    // mismatched call by swapping the ry/rz arguments, which changed the
    // yaw math from what TarkovMonitor actually executes.
    private static float QuaternionToYaw(float x, float z, float y, float w)
    {
        var sinyCosp = 2.0f * (w * z + x * y);
        var cosyCosp = 1.0f - 2.0f * (y * y + z * z);
        var yaw = MathF.Atan2(sinyCosp, cosyCosp);
        return yaw * (180f / MathF.PI);
    }
}
