using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MacMirrorReceiver;

internal readonly struct SemanticVersion : IComparable<SemanticVersion>
{
	private static readonly Regex VersionPattern = new Regex(
		@"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.(?<revision>\d+))?(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+.*)?$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private readonly string? _preRelease;

	private SemanticVersion(int major, int minor, int patch, int revision, string? preRelease)
	{
		Major = major;
		Minor = minor;
		Patch = patch;
		Revision = revision;
		_preRelease = string.IsNullOrWhiteSpace(preRelease) ? null : preRelease;
	}

	public int Major { get; }
	public int Minor { get; }
	public int Patch { get; }
	public int Revision { get; }
	public bool IsPrerelease => _preRelease != null;

	public static bool TryParse(string? value, out SemanticVersion version)
	{
		version = default;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		Match match = VersionPattern.Match(value.Trim());
		if (!match.Success)
		{
			return false;
		}

		if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
			|| !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
			|| !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
		{
			return false;
		}

		int revision = 0;
		Group revisionGroup = match.Groups["revision"];
		if (revisionGroup.Success
			&& !int.TryParse(revisionGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out revision))
		{
			return false;
		}

		string? preRelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
		version = new SemanticVersion(major, minor, patch, revision, preRelease);
		return true;
	}

	public int CompareTo(SemanticVersion other)
	{
		int result = Major.CompareTo(other.Major);
		if (result != 0)
		{
			return result;
		}

		result = Minor.CompareTo(other.Minor);
		if (result != 0)
		{
			return result;
		}

		result = Patch.CompareTo(other.Patch);
		if (result != 0)
		{
			return result;
		}

		result = Revision.CompareTo(other.Revision);
		if (result != 0)
		{
			return result;
		}

		return ComparePreRelease(_preRelease, other._preRelease);
	}

	public override string ToString()
	{
		string text = $"{Major}.{Minor}.{Patch}";
		if (Revision != 0)
		{
			text += "." + Revision.ToString(CultureInfo.InvariantCulture);
		}
		if (_preRelease != null)
		{
			text += "-" + _preRelease;
		}
		return text;
	}

	private static int ComparePreRelease(string? left, string? right)
	{
		if (left == null && right == null)
		{
			return 0;
		}
		if (left == null)
		{
			return 1;
		}
		if (right == null)
		{
			return -1;
		}

		string[] leftParts = left.Split('.');
		string[] rightParts = right.Split('.');
		int length = Math.Max(leftParts.Length, rightParts.Length);
		for (int i = 0; i < length; i++)
		{
			if (i >= leftParts.Length)
			{
				return -1;
			}
			if (i >= rightParts.Length)
			{
				return 1;
			}

			string leftPart = leftParts[i];
			string rightPart = rightParts[i];
			bool leftNumeric = int.TryParse(leftPart, NumberStyles.None, CultureInfo.InvariantCulture, out int leftNumber);
			bool rightNumeric = int.TryParse(rightPart, NumberStyles.None, CultureInfo.InvariantCulture, out int rightNumber);
			if (leftNumeric && rightNumeric)
			{
				int numericResult = leftNumber.CompareTo(rightNumber);
				if (numericResult != 0)
				{
					return numericResult;
				}
				continue;
			}
			if (leftNumeric)
			{
				return -1;
			}
			if (rightNumeric)
			{
				return 1;
			}

			int lexicalResult = string.CompareOrdinal(leftPart, rightPart);
			if (lexicalResult != 0)
			{
				return lexicalResult;
			}
		}

		return 0;
	}
}
