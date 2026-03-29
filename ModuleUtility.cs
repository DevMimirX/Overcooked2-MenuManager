using System;
using HarmonyLib;

namespace HostUtilities
{
    internal static class ModuleUtility
    {
        public static Harmony RegisterHarmony(Type type)
        {
            Harmony harmony = Harmony.CreateAndPatchAll(type);
            _MODEntry.RegisterHarmony(type.Name, harmony);
            return harmony;
        }

        public static void Log(string message)
        {
            _MODEntry.LogInfo(message);
        }
    }
}
