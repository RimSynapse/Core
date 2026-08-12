using System;

namespace RimSynapse.Compat
{
    /// <summary>
    /// A RimSynapse version — <c>Release.Major.Minor.Patch</c> (production epoch . Core-milestone .
    /// feature . fix). Parses 1–4 dotted numeric segments (missing trailing segments read as 0),
    /// tolerates a leading 'v', and compares segment by segment. See the versioning scheme docs.
    /// </summary>
    public sealed class SynapseVersion : IComparable<SynapseVersion>
    {
        public readonly int Release;
        public readonly int Major;
        public readonly int Minor;
        public readonly int Patch;

        private SynapseVersion(int release, int major, int minor, int patch)
        {
            Release = release;
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        /// <summary>Parses "a", "a.b", "a.b.c" or "a.b.c.d" (optionally 'v'-prefixed). Missing segments are 0.</summary>
        public static bool TryParse(string s, out SynapseVersion version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(s)) return false;

            string t = s.Trim();
            if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t.Substring(1);

            string[] parts = t.Split('.');
            if (parts.Length == 0 || parts.Length > 4) return false;

            int[] nums = new int[4];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), out int n) || n < 0) return false;
                nums[i] = n;
            }

            version = new SynapseVersion(nums[0], nums[1], nums[2], nums[3]);
            return true;
        }

        public int CompareTo(SynapseVersion other)
        {
            if (other == null) return 1;
            int c = Release.CompareTo(other.Release); if (c != 0) return c;
            c = Major.CompareTo(other.Major); if (c != 0) return c;
            c = Minor.CompareTo(other.Minor); if (c != 0) return c;
            return Patch.CompareTo(other.Patch);
        }

        public override string ToString() => $"{Release}.{Major}.{Minor}.{Patch}";
    }
}
