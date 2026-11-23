using System;
using System.Text.RegularExpressions;

internal static class ModeTapperVersion
{
    static readonly Regex TagFormat = new Regex(@"\Av(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){0,2}\z", RegexOptions.CultureInvariant);

    internal static string Display(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build)).ToString(3);
    }

    internal static bool TryReadTag(string tag, out Version version)
    {
        version = null;
        if (tag == null || !TagFormat.IsMatch(tag)) return false;
        string number = tag.Substring(1);
        if (number.IndexOf('.') < 0) number += ".0";
        Version parsed;
        if (!Version.TryParse(number, out parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), 0);
        return true;
    }
}
