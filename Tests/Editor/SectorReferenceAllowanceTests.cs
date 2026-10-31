using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace Caelix.Tests
{
    /// <summary>
    /// Tripwire for the brick-key facade: sector types are backend details of Caelix-Core, and no
    /// file outside it may name them. Both allowance lists are EMPTY since milestone 3 of
    /// <c>brick-key-facade-proposal.md</c> ported the last consumer, so this test now only guards
    /// against re-introduction. A stale entry fails too, which is what keeps a list from growing
    /// back: an entry may only be added together with the file that needs it, and never for new
    /// code.
    /// </summary>
    public class SectorReferenceAllowanceTests
    {
        private static readonly Regex SectorReference =
            new(@"SectorHandle|SectorNeighborHandles|\bSector\.", RegexOptions.Compiled);

        // Caelix package, relative to the package root, forward slashes. Empty since milestone 3.
        private static readonly string[] CaelixAllowance = System.Array.Empty<string>();

        // Physics package, relative to the package root. Empty since milestone 3.
        private static readonly string[] PhysicsAllowance = System.Array.Empty<string>();

        [Test]
        public void CaelixPackage_OnlyAllowedFilesNameSectorTypes()
        {
            string root = PackageRoot(typeof(Rendering.RayQuery.CaelixRayQueryRenderer).Assembly);
            Check(root, new[] { "Runtime", "Editor" }, CaelixAllowance);
        }

        [Test]
        public void PhysicsPackage_OnlyAllowedFilesNameSectorTypes()
        {
            string root = PackageRoot(typeof(VoxelBodyData).Assembly);
            Check(root, new[] { "Caelix.Physics/Runtime", "Unity.Physics" }, PhysicsAllowance);
        }

        private static string PackageRoot(System.Reflection.Assembly assembly)
        {
            PackageInfo info = PackageInfo.FindForAssembly(assembly);
            Assert.That(info, Is.Not.Null, $"{assembly.GetName().Name} is not in a package");
            return info.resolvedPath.Replace('\\', '/').TrimEnd('/');
        }

        private static void Check(string root, string[] scanDirs, string[] allowance)
        {
            var allowed = new HashSet<string>(allowance, StringComparer.Ordinal);
            var offenders = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string dir in scanDirs)
            {
                string full = root + "/" + dir;
                if (!Directory.Exists(full)) continue;

                foreach (string file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
                {
                    string relative = file.Replace('\\', '/').Substring(root.Length + 1);
                    if (!NamesSectorType(file)) continue;

                    seen.Add(relative);
                    if (!allowed.Contains(relative)) offenders.Add(relative);
                }
            }

            var stale = new List<string>();
            foreach (string entry in allowance)
            {
                if (!seen.Contains(entry)) stale.Add(entry);
            }

            Assert.That(offenders, Is.Empty,
                "New files outside Caelix-Core name sector types. Use the VoxelEntityData facade " +
                "(brick keys, TryBindBrick, EnumerateBricks, OpenNeighborhood, Changes) instead:\n  " +
                string.Join("\n  ", offenders));
            Assert.That(stale, Is.Empty,
                "These allowance entries no longer name sector types; remove them so the list keeps shrinking:\n  " +
                string.Join("\n  ", stale));
        }

        private static bool NamesSectorType(string path)
        {
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue; // comments and XML docs may explain history
                if (SectorReference.IsMatch(line)) return true;
            }

            return false;
        }
    }
}
