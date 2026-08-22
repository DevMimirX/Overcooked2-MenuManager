using System;
using System.IO;

namespace OC2MenuManager.Infrastructure
{
    internal static class UserDataMigration
    {
        public static bool CopyFirstExistingWhenDestinationMissing(string destinationPath, string[] sourcePaths)
        {
            if (string.IsNullOrEmpty(destinationPath))
            {
                throw new ArgumentException("A destination path is required.", nameof(destinationPath));
            }

            if (File.Exists(destinationPath) || sourcePaths == null)
            {
                return false;
            }

            for (int i = 0; i < sourcePaths.Length; i++)
            {
                string sourcePath = sourcePaths[i];
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    continue;
                }

                string destinationDirectory = Path.GetDirectoryName(destinationPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(sourcePath, destinationPath, false);
                return true;
            }

            return false;
        }
    }
}
