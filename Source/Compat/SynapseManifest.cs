using System;
using System.IO;
using System.Xml;

namespace RimSynapse.Compat
{
    /// <summary>
    /// A parsed <c>About/Manifest.xml</c> for a RimSynapse mod. The manifest is a companion to
    /// About.xml (which has no native version field): it declares this mod's version, the minimum
    /// Core it requires, and where to check for updates. Interoperable with the community
    /// "Version Checker" mod's Manifest.xml shape.
    /// </summary>
    public sealed class SynapseManifest
    {
        public string PackageId;
        public string ModName;
        public SynapseVersion Version;
        public SynapseVersion CoreFloor;   // null when the mod declares no Core dependency (e.g. Core itself)
        public string ManifestUri;
        public string DownloadUri;
        public string WorkshopId;
        public string RootDir;

        public bool IsCore =>
            PackageId != null && PackageId.Equals("rimsynapse.core", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads and parses <c>About/Manifest.xml</c> under <paramref name="rootDir"/>.
        /// Returns null if the file is absent, malformed, or has no valid &lt;version&gt;.
        /// </summary>
        public static SynapseManifest Load(string rootDir, string packageId, string modName)
        {
            try
            {
                string path = Path.Combine(rootDir, "About", "Manifest.xml");
                if (!File.Exists(path)) return null;

                var doc = new XmlDocument();
                doc.Load(path);
                XmlElement root = doc.DocumentElement;
                if (root == null) return null;

                string verStr = ChildText(root, "version");
                if (!SynapseVersion.TryParse(verStr, out SynapseVersion version))
                {
                    SynapseLogger.Warning($"[RimSynapse] Manifest.xml for {packageId} has no valid <version> (\"{verStr}\").");
                    return null;
                }

                SynapseVersion floor = null;
                string floorStr = ChildText(root, "coreVersion");
                if (!string.IsNullOrEmpty(floorStr))
                    SynapseVersion.TryParse(floorStr, out floor);

                return new SynapseManifest
                {
                    PackageId = packageId,
                    ModName = modName,
                    Version = version,
                    CoreFloor = floor,
                    ManifestUri = ChildText(root, "manifestUri"),
                    DownloadUri = ChildText(root, "downloadUri"),
                    WorkshopId = ChildText(root, "workshopId"),
                    RootDir = rootDir,
                };
            }
            catch (Exception e)
            {
                SynapseLogger.Warning($"[RimSynapse] Failed to read Manifest.xml for {packageId}: {e.Message}");
                return null;
            }
        }

        private static string ChildText(XmlElement root, string name)
        {
            XmlElement e = root[name];
            return e?.InnerText?.Trim();
        }
    }
}
