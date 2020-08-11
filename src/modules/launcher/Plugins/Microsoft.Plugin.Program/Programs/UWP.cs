// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Xml.Linq;
using Microsoft.Plugin.Program.Logger;
using Microsoft.Plugin.Program.Win32;
using Wox.Infrastructure.Logger;

namespace Microsoft.Plugin.Program.Programs
{
    [Serializable]
    public partial class UWP
    {
        public string Name { get; }

        public string FullName { get; }

        public string FamilyName { get; }

        public string Location { get; set; }

        public IList<UWPApplication> Apps { get; private set; }

        public PackageVersion Version { get; set; }

        public static IPackageManager PackageManagerWrapper { get; set; } = new PackageManagerWrapper();

        public UWP(IPackage package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            Name = package.Name;
            FullName = package.FullName;
            FamilyName = package.FamilyName;
        }

        public void InitializeAppInfo(string installedLocation)
        {
            Location = installedLocation;
            var path = Path.Combine(installedLocation, "AppxManifest.xml");

            var namespaces = XmlNamespaces(path);
            InitPackageVersion(namespaces);

            IStream stream;
            const uint noAttribute = 0x80;
            const Stgm exclusiveRead = Stgm.Read;
            var hResult = NativeMethods.SHCreateStreamOnFileEx(path, exclusiveRead, noAttribute, false, null, out stream);

            if (hResult == Hresult.Ok)
            {
                var apps = new List<UWPApplication>();

                List<IAppxManifestApplication> _apps = AppxPackageHelper.getAppsFromManifest(stream);
                foreach (var _app in _apps)
                {
                    var app = new UWPApplication(_app, this);
                    apps.Add(app);
                }

                Apps = apps.Where(a =>
                {
                    var valid =
                    !string.IsNullOrEmpty(a.UserModelId) &&
                    !string.IsNullOrEmpty(a.DisplayName) &&
                    a.AppListEntry != "none";

                    return valid;
                }).ToArray();
            }
            else
            {
                var e = Marshal.GetExceptionForHR((int)hResult);
                ProgramLogger.LogException($"|UWP|InitializeAppInfo|{path}" +
                                                "|Error caused while trying to get the details of the UWP program", e);

                Apps = new List<UWPApplication>().ToArray();
            }
        }

        /// http://www.hanselman.com/blog/GetNamespacesFromAnXMLDocumentWithXPathDocumentAndLINQToXML.aspx
        private static string[] XmlNamespaces(string path)
        {
            XDocument z = XDocument.Load(path);
            if (z.Root != null)
            {
                var namespaces = z.Root.Attributes().
                    Where(a => a.IsNamespaceDeclaration).
                    GroupBy(
                        a => a.Name.Namespace == XNamespace.None ? string.Empty : a.Name.LocalName,
                        a => XNamespace.Get(a.Value)
                    ).Select(
                        g => g.First().ToString()
                    ).ToArray();
                return namespaces;
            }
            else
            {
                Log.Error($"|UWP.XmlNamespaces|Error occurred while trying to get the XML from {path}");

                return Array.Empty<string>();
            }
        }

        private void InitPackageVersion(string[] namespaces)
        {
            var versionFromNamespace = new Dictionary<string, PackageVersion>
            {
                {"http://schemas.microsoft.com/appx/manifest/foundation/windows10", PackageVersion.Windows10},
                {"http://schemas.microsoft.com/appx/2013/manifest", PackageVersion.Windows81},
                {"http://schemas.microsoft.com/appx/2010/manifest", PackageVersion.Windows8},
            };

            foreach (var n in versionFromNamespace.Keys)
            {
                if (namespaces.Contains(n))
                {
                    Version = versionFromNamespace[n];
                    return;
                }
            }

            ProgramLogger.LogException($"|UWP|XmlNamespaces|{Location}" +
                                                "|Trying to get the package version of the UWP program, but a unknown UWP appmanifest version  "
                                                + $"{FullName} from location {Location} is returned.", new FormatException());

            Version = PackageVersion.Unknown;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Intentially keeping the process alive.")]
        public static UWPApplication[] All()
        {
            var windows10 = new Version(10, 0);
            var support = Environment.OSVersion.Version.Major >= windows10.Major;
            if (support)
            {
                var applications = CurrentUserPackages().AsParallel().SelectMany(p =>
                {
                    UWP u;
                    try
                    {
                        u = new UWP(p);
                        u.InitializeAppInfo(p.InstalledLocation);
                    }
                    catch (Exception e)
                    {
                        ProgramLogger.LogException($"|UWP|All|{p.InstalledLocation}|An unexpected error occurred and "
                                                        + $"unable to convert Package to UWP for {p.FullName}", e);
                        return Array.Empty<UWPApplication>();
                    }
                    return u.Apps;
                }).ToArray();

                var updatedListWithoutDisabledApps = applications
                                                        .Where(t1 => !Main._settings.DisabledProgramSources
                                                                        .Any(x => x.UniqueIdentifier == t1.UniqueIdentifier))
                                                        .Select(x => x);

                return updatedListWithoutDisabledApps.ToArray();
            }
            else
            {
                return Array.Empty<UWPApplication>();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Intentially keeping the process alive.")]
        private static IEnumerable<IPackage> CurrentUserPackages()
        {
            var ps = PackageManagerWrapper.FindPackagesForCurrentUser();
            ps = ps.Where(p =>
            {
                bool valid;
                try
                {
                    var f = p.IsFramework;
                    var path = p.InstalledLocation;
                    valid = !f && !string.IsNullOrEmpty(path);
                }
                catch (Exception e)
                {
                    ProgramLogger.LogException("UWP", "CurrentUserPackages", $"id", "An unexpected error occurred and "
                                                + $"unable to verify if package is valid", e);
                    return false;
                }

                return valid;
            });

            return ps;
        }

        public override string ToString()
        {
            return FamilyName;
        }

        public override bool Equals(object obj)
        {
            if (obj is UWP uwp)
            {
                return FamilyName.Equals(uwp.FamilyName, StringComparison.CurrentCultureIgnoreCase);
            }
            else
            {
                return false;
            }
        }

        public override int GetHashCode()
        {
            return FamilyName.GetHashCode(StringComparison.CurrentCultureIgnoreCase);
        }

        public enum PackageVersion
        {
            Windows10,
            Windows81,
            Windows8,
            Unknown
        }

        [Flags]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1714:Flags enums should have plural names", Justification = "This name is consistent with the corresponding win32 flags: https://docs.microsoft.com/en-us/windows/win32/stg/stgm-constants ")]
        public enum Stgm : long
        {
            Read = 0x00000000L,
        }

        public enum Hresult : int
        {
            Ok = 0x0,
        }

    }
}
