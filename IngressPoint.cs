using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Reflection;
using HarmonyLib;


namespace ExplosionNerf
{
    public static class IngressPoint
    {
        public static void Main()
        {
            ExplosionNerfMod.harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }
    public class ExplosionNerfMod : ModBase
    {
        const string HarmonyID = "flsoz.ttmm.explosionnerf.mod";
        internal static Harmony harmony = new Harmony(HarmonyID);

        public override bool HasEarlyInit()
        {
            return true;
        }

        internal static bool Inited = false;
        public override void EarlyInit()
        {
            if (!Inited)
            {
                string logLevelMod = CommandLineReader.GetArgument("+log_level_ExplosionNerf");
                string logLevelGeneral = CommandLineReader.GetArgument("+log_level");
                string logLevel = logLevelGeneral;
                if (logLevelMod != null)
                {
                    logLevel = logLevelMod;
                }
                if (logLevel != null)
                {
                    string lower = logLevel.ToLower();
                    if (lower == "trace" || lower == "debug")
                    {
                        Patches.Debug = true;
                    }
                    else
                    {
                        Patches.Debug = false;
                    }
                }
                else
                {
                    Patches.Debug = false;
                }

                Inited = true;
            }
        }

        public override void DeInit()
        {
            harmony.UnpatchAll(HarmonyID);
        }

        public override void Init()
        {
            IngressPoint.Main();
        }
    }
}
