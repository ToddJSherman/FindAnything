using System.Reflection;

// Shared by FindAnything.exe and FindAnything-Setup.exe. Shows in each file's Properties > Details.
[assembly: AssemblyProduct("Find Anything")]
[assembly: AssemblyCompany("ToddJSherman")]
[assembly: AssemblyCopyright("Copyright (c) 2026 ToddJSherman. MIT License.")]
[assembly: AssemblyVersion(FindAnything.AppInfo.Version)]
[assembly: AssemblyFileVersion(FindAnything.AppInfo.Version)]

namespace FindAnything
{
    internal static class AppInfo
    {
        public const string Version = "2026.10.11.3";
        public const string Name = "Find Anything";
        public const string Author = "ToddJSherman";
        public const string Url = "https://github.com/ToddJSherman/FindAnything";
        public const string License = "MIT License";
    }
}
