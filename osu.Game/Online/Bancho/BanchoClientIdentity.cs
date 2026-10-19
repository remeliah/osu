// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace osu.Game.Online.Bancho
{
    /// <summary>
    /// A stable-compatible client identity used by Bancho login and score submission.
    /// </summary>
    internal static class BanchoClientIdentity
    {
        private static readonly string osu_path_hash = getExecutableHash();
        private static readonly string adapters = getAdapters();
        private static readonly string adapters_hash = md5(adapters);

        // Stable's GameBase.UniqueId / UniqueId2 are already MD5 hashes. The login hash
        // applies MD5 once more, while score submission's c1 field uses these values directly.
        public static readonly string UninstallId = md5(getInstallIdentifier());
        public static readonly string DiskId = md5(getMachineIdentifier());

        private static readonly string uninstall_hash = md5(UninstallId);
        private static readonly string disk_hash = md5(DiskId);

        public static string LoginHashes => $"{osu_path_hash}:{adapters}:{adapters_hash}:{uninstall_hash}:{disk_hash}:";

        public static string ScoreClientHash => LoginHashes;

        private static string getExecutableHash()
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            }
            catch
            {
            }

            return md5(AppContext.BaseDirectory);
        }

        private static string getAdapters()
        {
            try
            {
                string value = string.Concat(NetworkInterface.GetAllNetworkInterfaces()
                                                             .Select(adapter => adapter.GetPhysicalAddress().ToString())
                                                             .Where(address => address.Length > 0)
                                                             .Select(address => $"{address}."));
                if (value.Length > 0)
                    return value;
            }
            catch
            {
            }

            return "runningunderwine";
        }

        private static string getInstallIdentifier()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    string? stableId = Registry.CurrentUser.OpenSubKey(@"Software\osu!")?.GetValue("UninstallID") as string;
                    if (!string.IsNullOrWhiteSpace(stableId))
                        return stableId;
                }
                catch
                {
                }
            }

            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu");
            string path = Path.Combine(directory, "stable-adapter-install-id");

            try
            {
                if (File.Exists(path))
                    return File.ReadAllText(path).Trim();

                Directory.CreateDirectory(directory);
                string id = Guid.NewGuid().ToString("D");
                File.WriteAllText(path, id);
                return id;
            }
            catch
            {
                return $"{Environment.UserName}:{AppContext.BaseDirectory}";
            }
        }

        private static string getMachineIdentifier()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    string? machineGuid = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid") as string;
                    if (!string.IsNullOrWhiteSpace(machineGuid))
                        return machineGuid;
                }
                catch
                {
                }
            }

            foreach (string path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                try
                {
                    if (File.Exists(path))
                        return File.ReadAllText(path).Trim();
                }
                catch
                {
                }
            }

            return Environment.MachineName;
        }

        private static string md5(string value) =>
            Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
