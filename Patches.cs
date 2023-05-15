using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System.Collections;
using HarmonyLib;
using System.Reflection;

namespace ExplosionNerf
{
    internal static class Patches
    {
        public static bool Debug = false;

        private static IEnumerable<DictionaryEntry> CastDict(IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                yield return entry;
            }
        }

        private static void DebugPrint(String prefix, String contents)
        {
            if (Debug)
            {
                d.Log(prefix + contents);
            }
        }

        // Get all blocks the explosion affects
        // ASSUMPTION: Explosions are handled sequentially on the main thread. i.e. only one Explosion.Explode() is running at any one time
        [HarmonyPatch(typeof(Explosion))]
        [HarmonyPatch("GatherVisibleHits")]
        public static class PatchExplosion
        {
            private static readonly FieldInfo hitDict = AccessTools.Field(typeof(Explosion), "s_VisibleHits");
            private static IDictionary s_VisibleHits;

            public static void Postfix(ref Explosion __instance)
            {
                DebugPrint("<ENM> ", "============================ NEW EXPLOSION ============================");
                // DebugPrint("initial");
                PatchDamage.hitBlock = null;
                PatchDamage.hitShield = null;
                PatchDamage.originalDamage = __instance.m_MaxDamageStrength;
                PatchExplosion.s_VisibleHits = (IDictionary)PatchExplosion.hitDict.GetValue(null);
                // DebugPrint("fetch");
                PatchDamage.castSource = __instance;
                if (PatchExplosion.s_VisibleHits != null && PatchExplosion.s_VisibleHits.Count > 0)
                {
                    Damageable directHit = (Damageable)PatchDamage.DirectHit.GetValue(__instance);
                    // if (directHit != null && directHit.Block != null && (directHit.MaxHealth >= 1.0f || directHit.Block.GetComponent<ModuleShieldGenerator>() == null))
                    if (directHit != null) {
                        if (directHit.Block != null && directHit.Block.tank != null && directHit.Block.visible.damageable == directHit)
                        {
                            PatchDamage.hitBlock = directHit.Block;
                        }
                        else if (directHit.m_DamageableType == ManDamage.DamageableType.Shield && directHit.Block != null && directHit.Block.visible.damageable != directHit)
                        {
                            // we have hit a shield, mark it
                            PatchDamage.hitShield = directHit;
                        }
                    }

                    // DebugPrint("condition");
                    Dictionary<int, object> newDictionary = CastDict(PatchExplosion.s_VisibleHits).ToDictionary(entry => (int)entry.Key, entry => entry.Value);
                    // DebugPrint("cast");
                    List<object> myList = newDictionary.Values.ToList();

                    PatchDamage.YetToHit.Clear();
                    foreach (HashSet<TankBlock> table in PatchDamage.DeterminedInvincible.Values)
                    {
                        table.Clear();
                    }
                    PatchDamage.DeterminedInvincible.Clear();
                    foreach (object obj in myList)
                    {
                        Visible visible = (Visible)obj.GetType().GetField("visible", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
                        if (visible != null)
                        {
                            TankBlock tentative = visible.block;
                            if (tentative != null)
                            {
                                PatchDamage.YetToHit.Add(tentative);
                            }
                        }
                    }
                }
            }
        }

        // Do the LOS/recursion on explode
        [HarmonyPatch(typeof(ManDamage))]
        [HarmonyPatch("DealDamage")]
        [HarmonyPatch(new Type[] { typeof(Damageable), typeof(float), typeof(ManDamage.DamageType), typeof(Component), typeof(Tank), typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) })]
        public static class PatchDamage
        {
            public static Explosion castSource;
            public static float originalDamage;
            public static TankBlock hitBlock;
            public static Damageable directHit;
            public static Damageable hitShield;
            public static HashSet<TankBlock> YetToHit = new HashSet<TankBlock>();
            public static Dictionary<Tank, HashSet<TankBlock>> DeterminedInvincible = new Dictionary<Tank, HashSet<TankBlock>>();

            public static readonly FieldInfo DirectHit = AccessTools.Field(typeof(Explosion), "m_DirectHitTarget");
            private static readonly FieldInfo m_AoEDamageBlockPercent = AccessTools.Field(typeof(Damageable), "m_AoEDamageBlockPercent");
            private static readonly FieldInfo m_DamageType = AccessTools.Field(typeof(Explosion), "m_DamageType");
            private static readonly FieldInfo m_DamageSource = AccessTools.Field(typeof(Explosion), "m_DamageSource");

            private static readonly FieldInfo m_ExplodeCountdownTimer = AccessTools.Field(typeof(ModuleDamage), "m_ExplodeCountdownTimer");
            // private static readonly FieldInfo rejectDamageEvent = AccessTools.Field(typeof(Damageable), "rejectDamageEvent");

            private static readonly FieldInfo m_DamageMultiplierTable = AccessTools.Field(typeof(ManDamage), "m_DamageMultiplierTable");
            private static readonly FieldInfo m_CabDamageDissipationFactor = AccessTools.Field(typeof(ManDamage), "m_CabDamageDissipationFactor");
            private static readonly FieldInfo m_CabDamageDissipationDetachFactor = AccessTools.Field(typeof(ManDamage), "m_CabDamageDissipationDetachFactor");
            private static readonly FieldInfo s_AdjacentBlocks = AccessTools.Field(typeof(ManDamage), "s_AdjacentBlocks");

            public static bool Prefix(ManDamage __instance, ref float __result, Damageable damageTarget, float damage, ManDamage.DamageType damageType, Component source, Tank sourceTank, Vector3 hitPosition, Vector3 damageDirection, float kickbackStrength, float kickbackDuration)
            {
                // DebugPrint("ASDF");
                // If we hit a shield, ensure we actually do damage to the shield
                if (hitShield != null)
                {
                    if (hitShield == damageTarget)
                    {
                        ManDamage.DamageInfo damageInfo = new ManDamage.DamageInfo(
                            damage,
                            damageType,
                            source,
                            sourceTank,
                            hitPosition,
                            damageDirection,
                            kickbackStrength,
                            kickbackDuration
                        );
                        __result = DoDamage(__instance, null, damageTarget, damageInfo);
                        return false;
                    }
                }
                // We still apply splash damage even when hitting a shield
                if (sourceTank != null && hitPosition != default && damageDirection != default)
                {
                    TankBlock targetBlock = damageTarget.Block;
                    // DebugPrint("<ENM> ", "Check 1");
                    if (targetBlock != null && source != null && source.GetType() == typeof(Explosion))
                    {
                        // DebugPrint("<ENM> ", "Check 2");
                        Damageable directHit = PatchDamage.hitBlock == null ? null : (Damageable)PatchDamage.DirectHit.GetValue(source);
                        Tank targetTank = targetBlock.tank;
                        if (targetTank != null && targetTank != sourceTank && targetTank.Team != sourceTank.Team)
                        {
                            // DebugPrint("<ENM> ", "Check 3");
                            // ManDamage.DamageInfo damageInfo = new ManDamage.DamageInfo(damage, damageType, source, sourceTank, hitPosition, damageDirection, kickbackStrength, kickbackDuration);
                            __result = recursiveHandleDamage(__instance, ref __result, damageTarget, directHit, source, targetBlock, targetTank, "<ENM> ");
                            return false;
                        }
                    }
                }
                return true;
            }

            private static ManDamage.DamageInfo NewDamageInfo(Damageable directHit, Damageable damageTarget, TankBlock targetBlock)
            {
                Vector3 actual = PatchDamage.castSource.transform.position - targetBlock.transform.position;
                // d.Log("1");
                float a = (float)(1.0 / ((double)PatchDamage.castSource.m_EffectRadius * (double)PatchDamage.castSource.m_EffectRadius));
                // d.Log("2");
                float b = (float)(1.0 / ((double)PatchDamage.castSource.m_EffectRadiusMaxStrength * (double)PatchDamage.castSource.m_EffectRadiusMaxStrength));
                // d.Log("3");
                float sqDist = actual.sqrMagnitude;
                // d.Log("4");
                float num1 = !((UnityEngine.Object)directHit != (UnityEngine.Object)null) || ((UnityEngine.Object)damageTarget == (UnityEngine.Object)directHit) ? Mathf.InverseLerp(a, b, 1f / sqDist) : 1f;
                // d.Log("5");
                float damage = PatchDamage.castSource.DoDamage ? PatchDamage.castSource.m_MaxDamageStrength * num1 : 0.0f;
                // d.Log("6");
                Vector3 damageDirection = (-actual).normalized * num1 * PatchDamage.castSource.m_MaxImpulseStrength;
                // d.Log("7");
                damage *= (0.5f + (float)targetBlock.GetComponent<ModuleDamage>().m_DamageDetachFragility);
                return new ManDamage.DamageInfo(damage, (ManDamage.DamageType)PatchDamage.m_DamageType.GetValue(PatchDamage.castSource), (Component)PatchDamage.castSource, (Tank)PatchDamage.m_DamageSource.GetValue(PatchDamage.castSource), targetBlock.transform.position, damageDirection, 0.0f, 0.0f);
            }

            private static float recursiveHandleDamage(ManDamage __instance, ref float __result, Damageable damageTarget, Damageable directHit, Component source, TankBlock targetBlock, Tank targetTank, string prefix, bool RecurseOverride = false)
            {
                if (RecurseOverride || PatchDamage.YetToHit.Contains(targetBlock))
                {

                    DebugPrint("\n" + prefix, "Resolving Block: " + targetBlock.name);
                    PatchDamage.YetToHit.Remove(targetBlock);

                    if (directHit != null)
                    {
                        DebugPrint(prefix, "direct hit detected");
                        TankBlock localHitBlock = directHit.Block;
                        if (localHitBlock != PatchDamage.hitBlock)
                        {
                            DebugPrint(prefix, "ONLY should happen when direct hit RESOLVED, block NOT destroyed");
                            DebugPrint(prefix, "RESOLUTION - ALWAYS BLOCK");
                            return 0.0f;
                        }

                        if (directHit == damageTarget)
                        {
                            // do actual dmg to targetBlock
                            DebugPrint(prefix, "Direct hit NEVER invincible - calculate dmg");

                            Explosion castSource = (Explosion)source;

                            // d.Log(directHit.MaxHealth <= 1.0f);
                            // d.Log(localHitBlock.GetComponent<ModuleShieldGenerator>() != null);
                            // d.Log(PatchDamage.rejectDamageEvent.GetValue());

                            ManDamage.DamageInfo damageInfo = PatchDamage.NewDamageInfo(directHit, damageTarget, targetBlock);
                            DebugPrint(prefix, "Damage: " + damageInfo.Damage.ToString());
                            float dmgDone = PatchDamage.DoDamage(__instance, directHit, damageTarget, damageInfo);
                            if (dmgDone == 0.0f)
                            {
                                DebugPrint(prefix, "Block NOT destroyed - mark invincible for now");
                                if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                                {
                                    PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                                }
                                PatchDamage.DeterminedInvincible[targetTank].Add(localHitBlock);
                            }
                            else
                            {
                                DebugPrint(prefix, "BLOCK DESTROYED - remove from consideration");
                                PatchDamage.DirectHit.SetValue(source, null);
                            }
                            PatchDamage.hitBlock = null;

                            // modify damageInfo for recursion purposes - no longer necessary
                            // damageInfo.ApplyDamageMultiplier(newDamageMult);

                            // modify dmg energy left in explosion
                            castSource.m_MaxDamageStrength *= 1.0f - (float)PatchDamage.m_AoEDamageBlockPercent.GetValue(directHit);
                            // PatchDamage.DirectHit.SetValue(source, null);
                            return dmgDone;
                        }
                        else
                        {
                            DebugPrint(prefix, "Resolve Direct hit first");
                            float destroyed = PatchDamage.recursiveHandleDamage(__instance, ref __result, directHit, directHit, source, localHitBlock, targetTank, prefix + "|  ", true);

                            // if block was actually destroyed
                            if (destroyed != 0.0f)
                            {
                                // do actual dmg to targetBlock
                                PatchDamage.hitBlock = null;
                                DebugPrint(prefix, "Resolve NOT NECESSARILY invincible - calculate dmg (re-recurse)");
                                PatchDamage.YetToHit.Add(targetBlock);
                                directHit = (Damageable)PatchDamage.DirectHit.GetValue(source);
                                return PatchDamage.recursiveHandleDamage(__instance, ref __result, damageTarget, directHit, source, targetBlock, targetTank, prefix + "|  ");
                            }
                            else
                            {
                                DebugPrint(prefix, "ONLY should happen when direct hit RESOLVED, block NOT destroyed");
                                DebugPrint(prefix, "RESOLUTION - ALWAYS BLOCK");
                                if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                                {
                                    PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                                }
                                PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);
                                return 0.0f;
                            }
                        }
                    }
                    else
                    {
                        DebugPrint(prefix, "no direct hit - Raycast time");
                        Vector3 actual = source.transform.position - targetBlock.transform.position;
                        RaycastHit[] results = new RaycastHit[((int)actual.magnitude)];
                        int hits = Physics.RaycastNonAlloc(new Ray(targetBlock.transform.position, actual), results, actual.magnitude, Singleton.Manager<ManVisible>.inst.VisiblePickerMaskNoTechs, QueryTriggerInteraction.Ignore);

                        List<TankBlock> foundBlocks = new List<TankBlock>();
                        if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                        {
                            PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                        }
                        for (int i = 0; i < hits; i++)
                        {
                            // DebugPrint("Loop " + i.ToString());
                            RaycastHit test = results[i];
                            Visible visible = Visible.FindVisibleUpwards((Component)test.collider);
                            if ((UnityEngine.Object)visible != (UnityEngine.Object)null)
                            {
                                TankBlock block = visible.block;
                                if (block != null && block != targetBlock && block.tank != null)
                                {
                                    if (block.tank == targetTank)
                                    {
                                        // DebugPrint("Damage blocked");
                                        if (PatchDamage.YetToHit.Contains(block))
                                        {
                                            DebugPrint(prefix, "  Unresolved block found: " + block.name);
                                            foundBlocks.Add(block);
                                        }
                                        else if (PatchDamage.DeterminedInvincible[targetTank].Contains(block))
                                        {
                                            DebugPrint(prefix, "  Resolved INVINCIBLE found");
                                            PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);
                                            foundBlocks.Clear();
                                            return 0.0f;
                                        }
                                    }
                                }
                            }
                        }
                        DebugPrint(prefix, "Raycast initial pass done - no invincible found in path");
                        if (foundBlocks.Count > 0)
                        {
                            bool breakInvincible = false;
                            foreach (TankBlock block in foundBlocks)
                            {
                                if (PatchDamage.YetToHit.Contains(block))
                                {
                                    float destroyed = PatchDamage.recursiveHandleDamage(__instance, ref __result, block.GetComponent<Damageable>(), directHit, source, block, targetTank, prefix + "|  ");

                                    if (destroyed == 0.0f)
                                    {
                                        PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);

                                        breakInvincible = true;
                                        break;
                                    }
                                }
                                else if (PatchDamage.DeterminedInvincible[targetTank].Contains(block))
                                {
                                    PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);

                                    breakInvincible = true;
                                    DebugPrint(prefix, "Resolved INVINCIBLE found");
                                    break;
                                }
                            }

                            if (!breakInvincible)
                            {
                                DebugPrint(prefix, "(A) [Should be rare] Resolve NOT invincible - calculate dmg");
                                ManDamage.DamageInfo damageInfo = PatchDamage.NewDamageInfo(directHit, damageTarget, targetBlock);
                                DebugPrint(prefix, "Damage: " + damageInfo.Damage.ToString());
                                float dmgDone = PatchDamage.DoDamage(__instance, directHit, damageTarget, damageInfo);
                                if (dmgDone == 0.0f)
                                {
                                    DebugPrint(prefix, "Block NOT destroyed - mark invincible for now");
                                    if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                                    {
                                        PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                                    }
                                    PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);
                                }
                                else
                                {
                                    DebugPrint(prefix, "BLOCK DESTROYED - remove from consideration");
                                }
                                return dmgDone;
                            }
                            else
                            {
                                DebugPrint(prefix, "Resolved INVINCIBLE found");
                                return 0.0f;
                            }
                        }
                        else
                        {
                            DebugPrint(prefix, "No blocks in way - Resolve NOT invincible - calculate dmg");
                            ManDamage.DamageInfo damageInfo = PatchDamage.NewDamageInfo(directHit, damageTarget, targetBlock);
                            DebugPrint(prefix, "Damage: " + damageInfo.Damage.ToString());
                            float dmgDone = PatchDamage.DoDamage(__instance, directHit, damageTarget, damageInfo);
                            if (dmgDone == 0.0f)
                            {
                                DebugPrint(prefix, "Block NOT destroyed - mark invincible for now");
                                if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                                {
                                    PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                                }
                                PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);
                            }
                            else
                            {
                                DebugPrint(prefix, "BLOCK DESTROYED - remove from consideration");
                            }
                            return dmgDone;
                        }
                    }
                }
                DebugPrint(prefix, "END");
                return 0.0f;
            }

            private static float DoDamage(ManDamage __instance, Damageable directHit, Damageable damageTarget, ManDamage.DamageInfo damageInfo)
            {
                DebugPrint("<ENM> ", "Doing DMG");

                float cabDamageDissipationFactor = (float)PatchDamage.m_CabDamageDissipationFactor.GetValue(__instance);
                // DebugPrint("A");

                float dmgDone = 0.0f;

                ManDamage.DamageInfo damageInfo1 = damageInfo.Clone();

                if (directHit != null)
                {
                    if ((UnityEngine.Object)PatchDamage.m_DamageMultiplierTable.GetValue(__instance) != (UnityEngine.Object)null)
                    {
                        // DebugPrint("B1");
                        float damageMultiplier = ((DamageMultiplierTable)PatchDamage.m_DamageMultiplierTable.GetValue(__instance)).GetDamageMultiplier(damageInfo.DamageType, directHit.DamageableType, true);
                        // DebugPrint("B2");
                        if ((double)damageMultiplier != 1.0)
                            damageInfo1.ApplyDamageMultiplier(damageMultiplier);
                        // DebugPrint("B3");
                    }

                    TankBlock hitBlock = damageTarget.Block;
                    if (hitBlock != null)
                    {
                        damageInfo1.ApplyDamageMultiplier(Mathf.Pow(hitBlock.filledCells.Length, 2.0f / 3.0f));
                    }

                    // DebugPrint("D");
                    int adjCount = 0;
                    if ((double)cabDamageDissipationFactor > 0.0 && directHit.Block.IsNotNull() && directHit.Block.IsController)
                    {
                        // DebugPrint("E1");
                        adjCount = directHit.Block.ConnectedBlocksByAP.Count();
                    }

                    if (adjCount > 0)
                    {
                        // DebugPrint("F1");
                        float cabDamageDissipationDetachFactor = (float)PatchDamage.m_CabDamageDissipationDetachFactor.GetValue(__instance);
                        // DebugPrint("F2");
                        // ManDamage.DamageInfo damageInfo2 = damageInfo1.Clone();
                        float multiplier = cabDamageDissipationFactor / (float)(adjCount + 1);
                        damageInfo1.ApplyDamageMultiplier((float)(1.0 - (double)multiplier * (double)adjCount));
                    }
                }
                dmgDone = damageTarget.TryToDamage(damageInfo1, true);

                //  || damageTarget.Block == null || damageTarget.Block.tank == null
                if (dmgDone == 0.0)
                {
                    if (damageTarget == null || damageTarget.Health <= 0.0f || damageTarget.Block == null || damageTarget.Block.PreExplodePulse || damageTarget.Block.tank == null)
                    {
                        dmgDone = 1.0f;
                    }
                    else
                    {
                        ModuleDamage module = damageTarget.Block.GetComponent<ModuleDamage>();
                        if (module != null)
                        {
                            dmgDone = (float)PatchDamage.m_ExplodeCountdownTimer.GetValue(module);
                        }
                    }
                }
                DebugPrint("<ENM> ", "Dmg Done: " + dmgDone.ToString());

                return dmgDone;
            }
        }

        // Restore explosion damage to original after explosion
        [HarmonyPatch(typeof(Explosion), "Explode")]
        public static class PatchExplosionDmg
        {
            public static void Postfix(ref Explosion __instance)
            {
                if (__instance == PatchDamage.castSource)
                {
                    __instance.m_MaxDamageStrength = PatchDamage.originalDamage;
                }
                else
                {
                    d.Log("<ENM> ERROR: EXPLOSIONS NOT EQUAL");
                }
            }
        }
    }
}
